using System.Linq;

using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Sounds;

using CSRoll.Config;
using CSRoll.Modifiers;

namespace CSRoll.Core;

public static partial class CSRollUtils
{
    /// <summary>Single source of truth for the admin permission string - referenced by command registration (CSRoll.Commands.cs) and by the admin-only chat helpers below.</summary>
    public const string AdminPermission = "gamemodifiers.admin";

    private static string _titlePrefix = "[CSRoll] ";

    /// <summary>
    /// Cross-modifier shared state: which player slots currently have x-ray vision (Wallhack).
    /// GameModifierInvisibleBase (ConditionalInvisibility/Vanish) reads this to exempt
    /// x-ray-enabled viewers from its own transmit-block entirely - the same technique already used
    /// to exempt spectators, applied here because "wallhack lets you see through walls" reasonably
    /// ought to include seeing through an invisibility effect too. Simpler and more direct than an
    /// earlier attempt at reworking how the x-ray glow effect's own entities transmit (a test-only
    /// fork, removed - didn't behave as wanted).
    /// </summary>
    private static readonly HashSet<int> _xrayVisionSlots = [];

    /// <summary>Fired immediately on grant/revoke (not just at the next spawn/death) so GameModifierInvisibleBase can resync a viewer's block state the instant Wallhack activates/deactivates for them mid-round.</summary>
    public static event Action<int>? XrayVisionGranted;

    /// <summary>See XrayVisionGranted.</summary>
    public static event Action<int>? XrayVisionRevoked;

    public static void GrantXrayVision(int slot)
    {
        _xrayVisionSlots.Add(slot);
        XrayVisionGranted?.Invoke(slot);
    }

    public static void RevokeXrayVision(int slot)
    {
        _xrayVisionSlots.Remove(slot);
        XrayVisionRevoked?.Invoke(slot);
    }

    public static bool HasXrayVision(int slot) => _xrayVisionSlots.Contains(slot);

    /// <summary>
    /// Bug fix: _xrayVisionSlots is a static field, independent of the plugin instance lifecycle - if
    /// the assembly stays resident across an Unload()/Load() (hot reload), stale x-ray-vision slot
    /// flags from the previous session could leak into the new one and wrongly exempt whichever new
    /// player later occupies that slot from GameModifierInvisibleBase's transmit-block. Called from
    /// CSRoll.Unload() so a fresh load always starts from zero granted slots.
    /// </summary>
    public static void ClearXrayVision() => _xrayVisionSlots.Clear();

    /// <summary>
    /// Sets the chat title prefix from config's BannerText, resolving SwiftlyS2's [colorname]
    /// chat color tokens (e.g. "[green]...[default]") via Helper.Colored(). SwiftlyS2 uses square
    /// brackets, not CounterStrikeSharp's curly braces - confirmed via the official porting guide
    /// (swiftlys2.net/docs/guides/porting-from-css) - a "{colorname}" token is left as literal text.
    /// Called once on config load and again on every hot-reload.
    /// </summary>
    public static void SetTitlePrefix(string bannerText)
    {
        _titlePrefix = SwiftlyS2.Shared.Helper.Colored(bannerText);
    }

    /// <summary>Ranged weapon categories - excludes WEAPONTYPE_KNIFE and WEAPONTYPE_C4, so strippers built on this set always leave a player's knife and bomb interactions untouched.</summary>
    public static readonly HashSet<CSWeaponType> AllRangedWeaponTypes =
    [
        CSWeaponType.WEAPONTYPE_PISTOL, CSWeaponType.WEAPONTYPE_SUBMACHINEGUN, CSWeaponType.WEAPONTYPE_RIFLE,
        CSWeaponType.WEAPONTYPE_SHOTGUN, CSWeaponType.WEAPONTYPE_SNIPER_RIFLE, CSWeaponType.WEAPONTYPE_MACHINEGUN,
        CSWeaponType.WEAPONTYPE_TASER, CSWeaponType.WEAPONTYPE_GRENADE,
    ];

    /// <summary>
    /// AllRangedWeaponTypes plus the knife - "literally everything except the bomb", for a total
    /// disarm (Vanish).
    ///
    /// Used sparingly and deliberately: GameModifierWalkingGrenadier documents that stripping the knife
    /// was tried and reverted, because CS2 doesn't handle a player holding nothing at all cleanly
    /// (it confuses weapon-switch/inventory state). That's survivable for a brief, self-reverting
    /// window in a way it wasn't for a whole-round restriction - hence Vanish.RemoveKnife exists as
    /// an escape hatch if it misbehaves live. WEAPONTYPE_C4 is still excluded, so the bomb (and
    /// therefore plant/defuse) is never affected.
    /// </summary>
    public static readonly HashSet<CSWeaponType> AllRangedAndKnifeWeaponTypes =
    [
        CSWeaponType.WEAPONTYPE_PISTOL, CSWeaponType.WEAPONTYPE_SUBMACHINEGUN, CSWeaponType.WEAPONTYPE_RIFLE,
        CSWeaponType.WEAPONTYPE_SHOTGUN, CSWeaponType.WEAPONTYPE_SNIPER_RIFLE, CSWeaponType.WEAPONTYPE_MACHINEGUN,
        CSWeaponType.WEAPONTYPE_TASER, CSWeaponType.WEAPONTYPE_GRENADE, CSWeaponType.WEAPONTYPE_KNIFE,
    ];

    /// <summary>Same as AllRangedWeaponTypes but excludes WEAPONTYPE_GRENADE - "every gun, no grenades, no knife" for modifiers that want to restrict firearms while leaving utility untouched (WalkingGrenadier, WeaponRoulette).</summary>
    public static readonly HashSet<CSWeaponType> AllGunWeaponTypes =
    [
        CSWeaponType.WEAPONTYPE_PISTOL, CSWeaponType.WEAPONTYPE_SUBMACHINEGUN, CSWeaponType.WEAPONTYPE_RIFLE,
        CSWeaponType.WEAPONTYPE_SHOTGUN, CSWeaponType.WEAPONTYPE_SNIPER_RIFLE, CSWeaponType.WEAPONTYPE_MACHINEGUN,
        CSWeaponType.WEAPONTYPE_TASER,
    ];

    /// <summary>Removes every weapon of the given types from a player, returning the removed item names so the caller can restore them later via RestoreWeapons.</summary>
    public static List<string> StripWeaponTypes(IPlayer player, HashSet<CSWeaponType> typesToStrip)
    {
        var removed = new List<string>();
        if (player.PlayerPawn?.WeaponServices is not { } weaponServices)
        {
            return removed;
        }

        foreach (var weapon in weaponServices.MyValidWeapons.ToList())
        {
            var weaponType = weapon.PlayerWeaponVData?.As<CCSWeaponBaseVData>().WeaponType;
            if (weaponType is not { } type || !typesToStrip.Contains(type))
            {
                continue;
            }

            // Bug fix: RemoveWeapon only detaches the weapon from the player - the underlying
            // entity isn't destroyed, so it was left sitting on the ground as a live, pickup-able
            // world entity (harmless for RandomLoadout/WalkingGrenadier, which only strip once per
            // life, but WeaponRoulette re-strips every reroll and was littering the map with
            // abandoned guns). Despawn() actually removes it.
            removed.Add(weapon.DesignerName);
            weaponServices.RemoveWeapon(weapon);

            try
            {
                weapon.Despawn();
            }
            catch (InvalidOperationException)
            {
                // Bug fix: confirmed via live server logs (not a guess) - Despawn() throws "The
                // entity instance is no longer valid" whenever RemoveWeapon's own detach already
                // triggered the engine's cleanup for that weapon entity (observed for WeaponRoulette,
                // which re-strips every reroll instead of once per life, making the race far more
                // likely to hit than for RandomLoadout/WalkingGrenadier). Already-gone is the goal state
                // Despawn() was trying to reach anyway, so this is not a real failure - but left
                // uncaught, the exception propagated out of this whole method and up through every
                // caller: OnPlayerSpawn (crashed the spawn hook), OnGameTick (crashed the tick,
                // aborting mid-reroll before the new weapon was ever given - "landed on AWP but
                // nothing came up"), and worst of all GameModifierBase.Activate() itself, where
                // IsActive and AssignedSlots are set BEFORE OnEnabled() runs - an exception here left
                // the modifier fully live and ticking while the caller's own follow-up
                // (ModifierRuntime adding it to _activeModifiers) never ran, making it permanently
                // invisible to !removemodifier(s). This one try/catch was the actual root cause of
                // every remaining WeaponRoulette symptom.
            }
        }

        return removed;
    }

    /// <summary>A stripped weapon plus the ammo it had at the moment it was taken away, so it can be handed back in the same state rather than freshly topped up. ItemDefinitionIndex pins down WHICH weapon it actually was - see GiveNameForItemDefinition.</summary>
    public sealed record StrippedWeapon(string DesignerName, ushort ItemDefinitionIndex, int Clip1, int ReserveAmmo);

    /// <summary>
    /// Item definition indices whose entity designer name is ambiguous, mapped to the name that
    /// actually gives that exact weapon back.
    ///
    /// Bug fix, reported live: a stripped USP-S came back as a P2000 and an M4A1-S came back as an
    /// M4A4. Several CS2 weapons share a loadout slot with a sibling, and the silenced variant is
    /// implemented as a subclass of the base - so the entity's DesignerName is the BASE name
    /// ("weapon_m4a1" is literally the M4A4's classname), and giving that name back yields the
    /// sibling rather than what the player was carrying. The definition index is the only field that
    /// distinguishes them, so restores resolve the give-name through this table first and only fall
    /// back to DesignerName for unambiguous weapons.
    /// </summary>
    private static readonly Dictionary<ushort, string> GiveNameForItemDefinition = new()
    {
        [16] = "weapon_m4a1",            // M4A4
        [60] = "weapon_m4a1_silencer",   // M4A1-S
        [32] = "weapon_hkp2000",         // P2000
        [61] = "weapon_usp_silencer",    // USP-S
        [1] = "weapon_deagle",           // Desert Eagle
        [64] = "weapon_revolver",        // R8 Revolver
        [33] = "weapon_mp7",             // MP7
        [23] = "weapon_mp5sd",           // MP5-SD
    };

    /// <summary>The name that gives this exact weapon back - the definition-index mapping where one exists, otherwise the entity's own designer name.</summary>
    private static string ResolveGiveName(StrippedWeapon weapon)
        => GiveNameForItemDefinition.TryGetValue(weapon.ItemDefinitionIndex, out var name) ? name : weapon.DesignerName;

    /// <summary>Reads a weapon's econ definition index, or 0 when it can't be resolved (in which case restores just fall back to the designer name).</summary>
    private static ushort TryGetItemDefinitionIndex(CBasePlayerWeapon weapon)
    {
        try
        {
            return weapon.AttributeManager.Item.ItemDefinitionIndex;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// StripWeaponTypes, but also snapshots each weapon's ammo.
    ///
    /// Needed by anything that takes weapons away temporarily and gives them straight back: plain
    /// RestoreWeapons re-gives via GiveItem, which hands over a weapon at full default ammo, so a
    /// short disarm would silently double as a free full reload.
    /// </summary>
    public static List<StrippedWeapon> StripWeaponTypesWithAmmo(IPlayer player, HashSet<CSWeaponType> typesToStrip)
    {
        var removed = new List<StrippedWeapon>();
        if (player.PlayerPawn?.WeaponServices is not { } weaponServices)
        {
            return removed;
        }

        foreach (var weapon in weaponServices.MyValidWeapons.ToList())
        {
            var weaponType = weapon.PlayerWeaponVData?.As<CCSWeaponBaseVData>().WeaponType;
            if (weaponType is not { } type || !typesToStrip.Contains(type))
            {
                continue;
            }

            var reserve = 0;
            try
            {
                reserve = weapon.ReserveAmmo[0];
            }
            catch (Exception)
            {
                // Some weapon types (knife especially) have no reserve slot to read - treat as zero
                // rather than letting it escape and abort the whole strip, which is the failure mode
                // StripWeaponTypes' own Despawn try/catch documents at length.
            }

            removed.Add(new StrippedWeapon(weapon.DesignerName, TryGetItemDefinitionIndex(weapon), weapon.Clip1, reserve));
            weaponServices.RemoveWeapon(weapon);

            try
            {
                weapon.Despawn();
            }
            catch (InvalidOperationException)
            {
                // See StripWeaponTypes for the full explanation - already-gone is the goal state.
            }
        }

        return removed;
    }

    /// <summary>
    /// Counterpart to StripWeaponTypesWithAmmo: re-gives each weapon then writes its snapshotted ammo
    /// back over the default full clip GiveItem hands out.
    ///
    /// The ammo write is deferred a tick - the weapon entity doesn't exist yet at the moment GiveItem
    /// is called, so writing Clip1 immediately would land on nothing.
    /// </summary>
    public static void RestoreWeaponsWithAmmo(ISwiftlyCore core, IPlayer player, IReadOnlyList<StrippedWeapon> weapons)
    {
        if (player.PlayerPawn?.ItemServices is not { } itemServices)
        {
            return;
        }

        foreach (var weapon in weapons)
        {
            itemServices.GiveItem(ResolveGiveName(weapon));
        }

        var slot = player.Slot;
        core.Scheduler.NextWorldUpdate(() =>
        {
            if (core.PlayerManager.GetPlayer(slot) is not { IsValid: true } restored ||
                restored.PlayerPawn?.WeaponServices is not { } weaponServices)
            {
                return;
            }

            // Matched on definition index where both sides resolved one, falling back to designer
            // name - the index is what actually distinguishes a USP-S from a P2000, which share a
            // designer name. A player can't carry two of the same weapon, so this can't mis-pair.
            foreach (var live in weaponServices.MyValidWeapons.ToList())
            {
                var liveDefinition = TryGetItemDefinitionIndex(live);
                var snapshot = weapons.FirstOrDefault(w => w.ItemDefinitionIndex != 0 && w.ItemDefinitionIndex == liveDefinition)
                    ?? weapons.FirstOrDefault(w => w.DesignerName == live.DesignerName);

                if (snapshot is null)
                {
                    continue;
                }

                live.Clip1 = snapshot.Clip1;
                live.Clip1Updated();

                try
                {
                    live.ReserveAmmo[0] = snapshot.ReserveAmmo;
                    live.ReserveAmmoUpdated();
                }
                catch (Exception)
                {
                    // Same as the read side - not every weapon has a reserve slot.
                }
            }
        });
    }

    /// <summary>Gives back a previously-stripped set of item names to a player (counterpart to StripWeaponTypes).</summary>
    public static void RestoreWeapons(IPlayer player, IEnumerable<string> itemNames)
    {
        if (player.PlayerPawn?.ItemServices is not { } itemServices)
        {
            return;
        }

        foreach (var item in itemNames)
        {
            itemServices.GiveItem(item);
        }
    }

    /// <summary>Picks a random dead (non-alive) player on the given team, or null if everyone on that team is alive/absent.</summary>
    public static IPlayer? GetRandomDeadTeammate(ISwiftlyCore core, Team team)
    {
        var deadTeammates = core.PlayerManager.GetInTeam(team).Where(p => p.IsValid && !p.IsAlive).ToList();
        return deadTeammates.Count > 0 ? deadTeammates[Random.Shared.Next(deadTeammates.Count)] : null;
    }

    /// <summary>
    /// Plays a soundevent that's already built into the game (no custom sound asset needed) to one
    /// player only - confirmed via SDK reflection: SwiftlyS2.Shared.Sounds.SoundEvent takes a
    /// soundevent name directly, and CRecipientFilter.AddRecipient(slot) scopes who hears it before
    /// Emit() fires it. An unknown/invalid soundevent name just silently does nothing (no exception),
    /// so a bad name here fails quietly rather than crashing anything - debugMode logs the emitted
    /// GUID so "the call ran but nothing was heard" can be told apart from "this never even fired".
    /// </summary>
    public static void PlaySoundToPlayer(ISwiftlyCore core, IPlayer player, string soundName, float volume = 1f, float pitch = 1f, bool debugMode = false)
    {
        if (string.IsNullOrEmpty(soundName))
        {
            return;
        }

        using var soundEvent = new SoundEvent(soundName, volume, pitch);
        soundEvent.Recipients.AddRecipient(player.Slot);
        var guid = soundEvent.Emit();

        if (debugMode)
        {
            core.Logger.LogInformation("[CSRoll] PlaySoundToPlayer: name={Name} slot={Slot} guid={Guid}", soundName, player.Slot, guid);
        }
    }

    /// <summary>Broadcast counterpart to PlaySoundToPlayer - same soundevent, heard by every currently connected player.</summary>
    public static void PlaySoundToAll(ISwiftlyCore core, string soundName, float volume = 1f, float pitch = 1f, bool debugMode = false)
    {
        if (string.IsNullOrEmpty(soundName))
        {
            return;
        }

        using var soundEvent = new SoundEvent(soundName, volume, pitch);
        soundEvent.Recipients.AddAllPlayers();
        var guid = soundEvent.Emit();

        if (debugMode)
        {
            core.Logger.LogInformation("[CSRoll] PlaySoundToAll: name={Name} guid={Guid}", soundName, guid);
        }
    }

    /// <summary>DMG_BULLET/DMG_BUCKSHOT are the flags CS2 uses for gunfire damage (as opposed to DMG_SLASH for knives or DMG_BLAST for explosives) - confirmed via SwiftlyS2.CS2.dll's DamageTypes_t enum.</summary>
    public static bool IsBulletDamage(DamageTypes_t damageType) =>
        (damageType & (DamageTypes_t.DMG_BULLET | DamageTypes_t.DMG_BUCKSHOT)) != 0;

    public static readonly IReadOnlyList<string> PistolNames =
    [
        "weapon_deagle", "weapon_elite", "weapon_fiveseven", "weapon_glock", "weapon_hkp2000",
        "weapon_p250", "weapon_tec9", "weapon_usp_silencer", "weapon_cz75a", "weapon_revolver",
    ];

    /// <summary>Pistols restricted to Terrorists only in standard CS2 (Glock, Tec-9, Dual Berettas).</summary>
    private static readonly HashSet<string> TOnlyPistols = ["weapon_glock", "weapon_tec9", "weapon_elite"];

    /// <summary>Pistols restricted to Counter-Terrorists only in standard CS2 (Five-SeveN, P2000, USP-S).</summary>
    private static readonly HashSet<string> CTOnlyPistols = ["weapon_fiveseven", "weapon_hkp2000", "weapon_usp_silencer"];

    public static readonly IReadOnlyList<string> MainWeaponNames =
    [
        "weapon_mac10", "weapon_mp5sd", "weapon_mp7", "weapon_mp9", "weapon_p90", "weapon_ump45",
        "weapon_bizon", "weapon_ak47", "weapon_aug", "weapon_famas", "weapon_galilar", "weapon_m4a1",
        "weapon_m4a1_silencer", "weapon_sg556", "weapon_ssg08", "weapon_awp", "weapon_g3sg1",
        "weapon_scar20", "weapon_nova", "weapon_xm1014", "weapon_mag7", "weapon_sawedoff",
        "weapon_m249", "weapon_negev",
    ];

    /// <summary>
    /// Bug fix: GetRandomMainWeaponName/GetRandomPistolName used to ignore team entirely - a real
    /// bug for anything that force-GiveItems the result, since CS2 enforces standard team weapon
    /// restrictions (M4A4/M4A1-S/AUG/FAMAS/MP7/MP9/SCAR-20/MAG-7 are CT-only; AK-47/Galil AR/SG 553/
    /// MAC-10/G3SG1/Sawed-Off are T-only) - a mismatched give can silently fail to arrive, leaving
    /// the player weapon-less. Both team-restriction sets below reflect CS2's standard default
    /// loadout rules; everything not listed in either is available to both teams.
    /// </summary>
    private static readonly HashSet<string> TOnlyMainWeapons = ["weapon_mac10", "weapon_ak47", "weapon_galilar", "weapon_sg556", "weapon_g3sg1", "weapon_sawedoff"];

    /// <summary>See TOnlyMainWeapons.</summary>
    private static readonly HashSet<string> CTOnlyMainWeapons = ["weapon_mp9", "weapon_mp7", "weapon_aug", "weapon_famas", "weapon_m4a1", "weapon_m4a1_silencer", "weapon_scar20", "weapon_mag7"];

    /// <summary>
    /// Bug fix: GetRandomMainWeaponName used to pick uniformly across all 24 entries above - reported
    /// live as feeling like it favored auto-snipers/AUG/SSG (all 1-in-24 like everything else, but
    /// the less exciting picks are simply the majority of the list). These four "meta" rifles get 3x
    /// the weight of everything else (AK47/M4A4/M4A1-S/AWP: 3 each = 12 of a 32 total, vs 1 each for
    /// the other 20) - noticeably more likely to come up, without making them guaranteed or removing
    /// any weapon from the pool entirely.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> MainWeaponWeights = new Dictionary<string, int>
    {
        ["weapon_ak47"] = 3,
        ["weapon_m4a1"] = 3,
        ["weapon_m4a1_silencer"] = 3,
        ["weapon_awp"] = 3,
    };

    /// <summary>"weapon_incendiary" is a marker, not a real item name - molotov/incendiary is the same grenade under two team-restricted classnames, resolved via ResolveGrenadeName right before it's actually given.</summary>
    public static readonly IReadOnlyList<string> GrenadeNames =
    [
        "weapon_hegrenade", "weapon_flashbang", "weapon_smokegrenade", "weapon_decoy", "weapon_incendiary",
    ];

    private static bool IsUsableByTeam(string weaponName, Team team, HashSet<string> tOnly, HashSet<string> ctOnly) =>
        team switch
        {
            Team.T => !ctOnly.Contains(weaponName),
            Team.CT => !tOnly.Contains(weaponName),
            _ => true,
        };

    public static string GetRandomPistolName(Team team)
    {
        var pool = PistolNames.Where(name => IsUsableByTeam(name, team, TOnlyPistols, CTOnlyPistols)).ToList();
        return pool.Count > 0 ? pool[Random.Shared.Next(pool.Count)] : PistolNames[Random.Shared.Next(PistolNames.Count)];
    }

    public static string GetRandomMainWeaponName(Team team)
    {
        var pool = MainWeaponNames.Where(name => IsUsableByTeam(name, team, TOnlyMainWeapons, CTOnlyMainWeapons)).ToList();
        if (pool.Count == 0)
        {
            pool = MainWeaponNames.ToList();
        }

        var totalWeight = pool.Sum(name => MainWeaponWeights.GetValueOrDefault(name, 1));
        var roll = Random.Shared.Next(totalWeight);

        var cumulative = 0;
        foreach (var name in pool)
        {
            cumulative += MainWeaponWeights.GetValueOrDefault(name, 1);
            if (roll < cumulative)
            {
                return name;
            }
        }

        return pool[^1];
    }

    public static List<string> GetRandomGrenadeNames(int count)
    {
        var names = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            names.Add(GrenadeNames[Random.Shared.Next(GrenadeNames.Count)]);
        }

        return names;
    }

    public static string ResolveGrenadeName(string name, Team team) =>
        name == "weapon_incendiary" ? (team == Team.T ? "weapon_molotov" : "weapon_incgrenade") : name;

    /// <summary>Explicit display names for every classname in PistolNames/MainWeaponNames - internal CS2 names don't map onto their real-world names cleanly (e.g. weapon_m4a1 is actually the M4A4, weapon_hkp2000 is the P2000), so a generic strip-and-capitalize transform alone would mislabel several of these.</summary>
    private static readonly IReadOnlyDictionary<string, string> WeaponDisplayNames = new Dictionary<string, string>
    {
        ["weapon_deagle"] = "Desert Eagle",
        ["weapon_elite"] = "Dual Berettas",
        ["weapon_fiveseven"] = "Five Seven",
        ["weapon_glock"] = "Glock",
        ["weapon_hkp2000"] = "P2000",
        ["weapon_p250"] = "P250",
        ["weapon_tec9"] = "Tec-9",
        ["weapon_usp_silencer"] = "USP-S",
        ["weapon_cz75a"] = "CZ75-Auto",
        ["weapon_revolver"] = "R8 Revolver",
        ["weapon_mac10"] = "MAC-10",
        ["weapon_mp5sd"] = "MP5-SD",
        ["weapon_mp7"] = "MP7",
        ["weapon_mp9"] = "MP9",
        ["weapon_p90"] = "P90",
        ["weapon_ump45"] = "UMP-45",
        ["weapon_bizon"] = "PP-Bizon",
        ["weapon_ak47"] = "AK-47",
        ["weapon_aug"] = "AUG",
        ["weapon_famas"] = "FAMAS",
        ["weapon_galilar"] = "Galil AR",
        ["weapon_m4a1"] = "M4A4",
        ["weapon_m4a1_silencer"] = "M4A1-S",
        ["weapon_sg556"] = "SG 553",
        ["weapon_ssg08"] = "SSG 08",
        ["weapon_awp"] = "AWP",
        ["weapon_g3sg1"] = "G3SG1",
        ["weapon_scar20"] = "SCAR-20",
        ["weapon_nova"] = "Nova",
        ["weapon_xm1014"] = "XM1014",
        ["weapon_mag7"] = "MAG-7",
        ["weapon_sawedoff"] = "Sawed-Off",
        ["weapon_m249"] = "M249",
        ["weapon_negev"] = "Negev",
    };

    /// <summary>Player-facing name for a weapon classname (e.g. "weapon_ump45" -> "UMP-45"). Falls back to stripping the "weapon_" prefix and title-casing each underscore-separated word for anything not in WeaponDisplayNames, so a future addition to the weapon pools never renders as a raw classname even if this map isn't updated for it.</summary>
    public static string GetFriendlyWeaponName(string weaponName)
    {
        if (WeaponDisplayNames.TryGetValue(weaponName, out var displayName))
        {
            return displayName;
        }

        var stripped = weaponName.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase) ? weaponName[7..] : weaponName;
        var words = stripped.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length > 0 ? char.ToUpperInvariant(word[0]) + word[1..] : word);

        return string.Join(' ', words);
    }

    public static void PrintTitleToChat(ISwiftlyCore core, SwiftlyS2.Shared.Players.IPlayer? player, string message)
    {
        player?.SendChat($"{_titlePrefix}{message}");
    }

    public static void PrintTitleToChatAll(ISwiftlyCore core, string message)
    {
        core.PlayerManager.SendChat($"{_titlePrefix}{message}");
    }

    /// <summary>
    /// Like PrintTitleToChat, but also resolves [colorname] tokens in the message body itself -
    /// PrintTitleToChat only ever resolves the cached _titlePrefix (from BannerText), so a per-call
    /// message with its own color tokens (e.g. "[gold]Your modifiers:[default]") needs this instead
    /// or the brackets are sent to chat as literal text.
    /// </summary>
    public static void PrintTitleToChatColored(ISwiftlyCore core, SwiftlyS2.Shared.Players.IPlayer? player, string coloredMessage)
    {
        player?.SendChat($"{_titlePrefix}{SwiftlyS2.Shared.Helper.Colored(coloredMessage)}");
    }

    public static void PrintToChatAll(ISwiftlyCore core, string message)
    {
        core.PlayerManager.SendChat(message);
    }

    /// <summary>
    /// Which specific player got which random modifier is only ever sent here - to connected
    /// admins, not broadcast to the whole server. Nobody else needs to see another player's rolled
    /// modifiers in chat (their own center-HTML banner already tells them privately).
    /// </summary>
    private static IEnumerable<SwiftlyS2.Shared.Players.IPlayer> GetAdmins(ISwiftlyCore core) =>
        core.PlayerManager.GetAllValidPlayers().Where(p => core.Permission.PlayerHasPermission(p.SteamID, AdminPermission));

    public static void PrintTitleToAdminsOnly(ISwiftlyCore core, string message)
    {
        foreach (var admin in GetAdmins(core))
        {
            PrintTitleToChat(core, admin, message);
        }
    }

    public static void PrintToAdminsOnly(ISwiftlyCore core, string message)
    {
        foreach (var admin in GetAdmins(core))
        {
            admin.SendChat(message);
        }
    }

    /// <summary>Sends a styled center-screen HTML message (Panorama rich text, not a real HTML page - see SwiftlyS2's HTML Styling guide for supported tags).</summary>
    public static void ShowMessageCentreAll(ISwiftlyCore core, string html, int durationMs = 5000)
    {
        core.PlayerManager.SendCenterHTML(html, durationMs);
    }

    /// <summary>
    /// Builds the "Activating Modifiers" center banner: a red title, then each activated modifier on
    /// its own line. Deliberately no fontSize class (reverted after v1.32.0's larger sizing was
    /// reported as too big) - default size, matching every other popup in this codebase.
    ///
    /// Uses the self-closed &lt;br/&gt; form. An older comment here claimed the bare &lt;br&gt; form
    /// breaks the panel because the markup is parsed as strict XML - that is wrong, and worth not
    /// carrying forward into new code: Valve's own shipped csgo_english.txt contains hundreds of bare
    /// &lt;br&gt; tags (and several genuinely unbalanced &lt;b&gt; tags) that render fine in the retail
    /// client, and SwiftlyS2's own br-handling regex deliberately accepts every form. It is a lenient
    /// HTML parser, not an XML one. &lt;br/&gt; is still perfectly valid, so nothing here needs to
    /// change - but the parser is not the constraint the old note assumed it was.
    /// </summary>
    /// <param name="descriptionProgress">
    /// When null the descriptions render normally. When set (0..1) they render mid-wipe at that
    /// progress instead, which is what the reveal's scramble animation drives frame by frame - the
    /// title and modifier names are identical either way, so only the description lines move.
    /// </param>
    public static string BuildActivatingModifiersHtml(ISwiftlyCore core, IReadOnlyCollection<GameModifierBase> modifiers, SpinRevealConfig? spinReveal = null, float? descriptionProgress = null)
    {
        var title = modifiers.Count == 1 ? "Activating Modifier:" : "Activating Modifiers:";
        var lines = new List<string>();

        if (BuildRevealImageTag(spinReveal) is { } imageTag)
        {
            lines.Add(imageTag);
        }

        lines.Add($"<span color=\"red\" class=\"fontWeight-Bold\">{title}</span>");

        foreach (var modifier in modifiers)
        {
            lines.Add($"<span color=\"gold\" class=\"fontWeight-Bold\">{GetModifierDisplayName(core, modifier)}</span>");

            if (!(spinReveal?.ShowDescription ?? false))
            {
                continue;
            }

            var description = GetModifierDescription(core, modifier);

            // Descriptions are authored for chat, so their "[green]" tokens have to be translated
            // rather than passed through - center-HTML would render them as literal text. The
            // scramble path strips them instead, since it indexes real character positions and would
            // otherwise shred the markup it just inserted.
            lines.Add(descriptionProgress is { } progress
                ? BuildScrambledDescriptionHtml(PlainTextFromChatColors(description), progress)
                : BuildDescriptionLineHtml(ConvertChatColorsToHtml(description)));
        }

        return string.Join("<br/>", lines);
    }

    /// <summary>
    /// The description line's shared styling, so the scramble frames and the final resolved line are
    /// visually identical apart from their text - anything that differs here would read as a jump at
    /// the moment the animation lands.
    /// </summary>
    public static string BuildDescriptionLineHtml(string descriptionHtml)
        => $"<span class=\"fontWeight-Bold {MonoFontClass}\">{descriptionHtml}</span>";

    /// <summary>
    /// Builds one frame of the description's left-to-right reveal wipe.
    ///
    /// Three zones, tracking a window that sweeps across the text: everything left of the window is
    /// already locked to its real character, everything inside it is randomized, and everything to
    /// the right is blank. So the line starts empty, churns through the middle, and resolves from
    /// the left - progress 0 renders nothing, progress 1 renders the full text.
    ///
    /// Spaces are never scrambled: keeping them intact preserves the word rhythm, so the line reads
    /// as text resolving rather than as an undifferentiated block of noise. The whole run is
    /// monospaced (see MonoFontClass) because Panorama's default font is proportional and per-frame
    /// glyph churn would otherwise make the line visibly change width on every frame.
    ///
    /// Takes plain text, not markup - callers must strip color tokens first (PlainTextFromChatColors),
    /// since this indexes real character positions and would otherwise scramble the tags themselves.
    /// </summary>
    public static string BuildScrambledDescriptionHtml(string plainText, float progress, int scrambleWindowChars = 6)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return BuildDescriptionLineHtml(string.Empty);
        }

        var clamped = Math.Clamp(progress, 0f, 1f);
        if (clamped >= 1f)
        {
            return BuildDescriptionLineHtml(plainText);
        }

        // The window starts fully off the left edge and ends fully past the right, so the first frame
        // is genuinely blank and the last locks the final character.
        var window = Math.Max(1, scrambleWindowChars);
        var head = (clamped * (plainText.Length + window)) - window;

        var builder = new System.Text.StringBuilder(plainText.Length);
        for (var i = 0; i < plainText.Length; i++)
        {
            if (plainText[i] == ' ')
            {
                builder.Append(' ');
                continue;
            }

            if (i < head)
            {
                builder.Append(plainText[i]);
            }
            else if (i < head + window)
            {
                builder.Append(ScrambleGlyphs[Random.Shared.Next(ScrambleGlyphs.Length)]);
            }
            else
            {
                builder.Append(' ');
            }
        }

        return BuildDescriptionLineHtml(System.Net.WebUtility.HtmlEncode(builder.ToString()));
    }

    /// <summary>Glyphs the scramble window cycles through - deliberately dense, similar-width symbols so the churn reads as noise rather than as almost-words.</summary>
    private const string ScrambleGlyphs = "!@#$%^&*<>/\\|=+-_?0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>
    /// Chat color tokens that descriptions in en.jsonc are written with, mapped to the color names
    /// Panorama's center-HTML accepts in a &lt;span color="..."&gt; attribute.
    ///
    /// These two systems are entirely separate: chat resolves "[green]" through
    /// SwiftlyS2.Shared.Helper.Colored() into a control character, while center-HTML has no notion of
    /// the token at all and would render the literal text "[green]" on screen. Descriptions are
    /// authored for chat, so anything routing one into HTML has to translate first - that's what
    /// ConvertChatColorsToHtml is for.
    ///
    /// Only names Panorama actually accepts are mapped; the rest fall through to
    /// PlainTextFromChatColors' strip path rather than emitting a color Panorama would ignore.
    /// </summary>
    private static readonly Dictionary<string, string> ChatColorToHtmlColor = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = "white", ["darkred"] = "darkred", ["green"] = "green", ["olive"] = "olive",
        ["lime"] = "lime", ["red"] = "red", ["gray"] = "grey", ["grey"] = "grey",
        ["yellow"] = "yellow", ["lightyellow"] = "yellow", ["silver"] = "silver",
        ["lightblue"] = "lightblue", ["blue"] = "blue", ["darkblue"] = "darkblue",
        ["purple"] = "purple", ["magenta"] = "magenta", ["lightred"] = "red",
        ["gold"] = "gold", ["orange"] = "orange", ["lightpurple"] = "purple",
        ["bluegrey"] = "grey",
    };

    [System.Text.RegularExpressions.GeneratedRegex(@"\[([a-zA-Z/]+)\]")]
    private static partial System.Text.RegularExpressions.Regex ChatColorTokenRegex();

    /// <summary>
    /// Translates a chat-authored string's "[green]...[default]" tokens into center-HTML
    /// &lt;span color="..."&gt; markup, so a description written for chat can be shown in a popup
    /// without leaking literal "[green]" text on screen.
    ///
    /// "[default]" and "[/]" close the current span rather than opening anything. An unrecognized
    /// bracketed word (e.g. "[CSRoll]") is deliberately left untouched - the same behaviour
    /// Helper.Colored() has in chat, which is what lets a literal "[CSRoll]" banner survive.
    /// Any span still open at the end is closed so the markup can't bleed into whatever follows it.
    /// </summary>
    public static string ConvertChatColorsToHtml(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var open = 0;
        var result = ChatColorTokenRegex().Replace(text, match =>
        {
            var token = match.Groups[1].Value;

            if (token is "default" or "/")
            {
                if (open == 0)
                {
                    return string.Empty;
                }

                open--;
                return "</span>";
            }

            if (!ChatColorToHtmlColor.TryGetValue(token, out var htmlColor))
            {
                return match.Value;
            }

            open++;
            return $"<span color=\"{htmlColor}\">";
        });

        return open > 0 ? result + string.Concat(Enumerable.Repeat("</span>", open)) : result;
    }

    /// <summary>
    /// Strips chat color tokens entirely, leaving just the readable text. Used by the scramble
    /// animation, which has to reason about real character positions - markup embedded mid-string
    /// would be scrambled as if it were content, corrupting the tags.
    /// </summary>
    public static string PlainTextFromChatColors(string text)
        => string.IsNullOrEmpty(text) ? text : ChatColorTokenRegex().Replace(text, match =>
            match.Groups[1].Value is "default" or "/" || ChatColorToHtmlColor.ContainsKey(match.Groups[1].Value)
                ? string.Empty
                : match.Value);

    /// <summary>
    /// The optional reveal logo, or null when none is configured (the default) - in which case no
    /// &lt;img&gt; tag is emitted at all, so a server that never sets RevealImageUrl behaves exactly
    /// as it did before the option existed and makes no outbound request from any client.
    /// Width/height are always emitted - see SpinRevealConfig.RevealImageWidth for why.
    /// </summary>
    private static string? BuildRevealImageTag(SpinRevealConfig? spinReveal)
    {
        if (spinReveal is not { } config || string.IsNullOrWhiteSpace(config.RevealImageUrl))
        {
            return null;
        }

        var width = Math.Max(1, config.RevealImageWidth);
        var height = Math.Max(1, config.RevealImageHeight);
        return $"<img src='{config.RevealImageUrl}' width='{width}' height='{height}'/>";
    }

    /// <summary>
    /// Builds the persistent spectator HUD: who's being watched, then each of their active
    /// modifiers - or a plain "No modifiers" line if they have none, so the popup still confirms
    /// it's tracking the right target rather than just disappearing.
    /// </summary>
    public static string BuildSpectatorHudHtml(ISwiftlyCore core, string targetName, IReadOnlyCollection<GameModifierBase> modifiers, bool showDescriptions = true)
    {
        // Escaped because this is a player-controlled Steam name going straight into markup: an
        // <img src='http://...'/> in a name would fire an outbound request from every spectator's
        // client, and an unbalanced <span color> would bleed colour through the rest of the panel.
        var lines = new List<string> { $"<span color=\"gold\" class=\"fontWeight-Bold\">Watching: {System.Net.WebUtility.HtmlEncode(targetName)}</span>" };

        if (modifiers.Count == 0)
        {
            lines.Add("<span class=\"fontWeight-Bold\">No modifiers</span>");
        }
        else
        {
            foreach (var modifier in modifiers)
            {
                lines.Add($"<span class=\"fontWeight-Bold\">{GetModifierDisplayName(core, modifier)}</span>");

                // Deliberately never scrambled here, unlike the reveal popup: this HUD is persistent
                // and re-sent on a timer while spectating, so an animation would restart endlessly.
                if (showDescriptions)
                {
                    lines.Add(BuildDescriptionLineHtml(ConvertChatColorsToHtml(GetModifierDescription(core, modifier))));
                }
            }
        }

        return string.Join("<br/>", lines);
    }

    /// <summary>Builds the green/red "Random Rounds Enabled/Disabled" center banner.</summary>
    public static string BuildRandomRoundsToggleHtml(bool enabled)
    {
        var color = enabled ? "green" : "red";
        var status = enabled ? "Enabled" : "Disabled";
        return $"<span color=\"{color}\" class=\"fontSize-l fontWeight-Bold\">Random Rounds {status}</span>";
    }

    /// <summary>
    /// Builds one "still spinning" frame of the slot-machine reveal - deliberately the exact same
    /// red-title/gold-name styling and sizing as BuildActivatingModifiersHtml, so the spin reads as
    /// part of the same reveal rather than a visually distinct effect that then gets swapped out.
    /// </summary>
    public static string BuildSpinFrameHtml(string name)
    {
        return "<span color=\"red\" class=\"fontWeight-Bold\">Rolling...</span><br/>" +
               $"<span color=\"gold\" class=\"fontWeight-Bold\">{name}</span>";
    }

    /// <summary>
    /// Panorama's default label font (notosans) is proportional, so any run of mixed glyphs changes
    /// width as its contents change. csgostyles.css - Valve's globally-loaded stylesheet - defines a
    /// set of genuinely monospaced families; this is the one used for anything that has to stay
    /// column-aligned (progress bars, padded numbers). Verified present in Valve's shipped CSS
    /// alongside mono-spaced-font/stratum-light-mono/stratum-bold-mono.
    /// </summary>
    public const string MonoFontClass = "stratum-regular-mono";

    /// <summary>
    /// Builds a two-line center-HTML status gauge: a colored label line, then a progress bar
    /// ("[████████░░░░░░░░░░░░] 42%"). Deliberately plain bold text with no "fontSize-l" class - the
    /// same size as BuildSpinFrameHtml's "Rolling..." frame - so every gauge popup (invisibility
    /// status, teleport cooldown, etc.) reads as one consistent HUD family rather than a mismatched
    /// larger element.
    ///
    /// The bar previously used the SAME glyph ('#') for every cell, expressing fill state purely
    /// through color, because a proportional font renders '#' and '-' at different widths - so a
    /// mixed-glyph bar visibly changed width as it filled, eventually wrapping the trailing "NN%"
    /// onto a third line. That workaround is retired: rendering the bar in MonoFontClass makes every
    /// glyph exactly one column wide, so distinct filled/empty glyphs can be used again for real
    /// shape contrast (readable even for a colorblind player, and legible at a glance rather than
    /// relying on a lime-vs-grey distinction alone). The percent stays padded to a constant
    /// 3-character field, which now actually holds its column instead of merely approximating it.
    /// </summary>
    public static string BuildGaugeHtml(string label, string labelColor, float ratio, string barColor, int barWidth = 20)
    {
        var clamped = Math.Clamp(ratio, 0f, 1f);
        var filled = (int)Math.Round(clamped * barWidth);
        var empty = barWidth - filled;
        var percent = (int)Math.Round(clamped * 100f);

        var filledSegment = filled > 0 ? $"<span color=\"{barColor}\">{new string('█', filled)}</span>" : "";
        var emptySegment = empty > 0 ? $"<span color=\"grey\">{new string('░', empty)}</span>" : "";

        return $"<span color=\"{labelColor}\" class=\"fontWeight-Bold\">{label}</span><br/>" +
               $"<span class=\"fontWeight-Bold {MonoFontClass}\">[{filledSegment}{emptySegment}] {percent,3}%</span>";
    }

    /// <summary>
    /// CS2 screen-fade flags (engine FFADE_* values). FADE_IN means "start at the given colour and
    /// fade back to normal", which is the shape we want for a brief flash punctuating a reveal;
    /// PURGE clears any fade already in flight so overlapping reveals can't stack into a long blackout.
    /// </summary>
    private const int FadeFlagIn = 0x0001;
    private const int FadeFlagPurge = 0x0010;

    /// <summary>
    /// Brief screen flash on one player, used to punctuate a modifier reveal. Parses FadeColor from
    /// its "R,G,B,A" config form; a malformed value disables the flash rather than throwing inside a
    /// reveal callback (this runs from a scheduler continuation, where an exception would abort the
    /// rest of the reveal).
    /// </summary>
    public static void SendRevealFade(ISwiftlyCore core, IPlayer player, SpinRevealConfig config)
    {
        if (!config.FadeOnReveal || !TryParseRgba(config.FadeColor, out var r, out var g, out var b, out var a))
        {
            return;
        }

        core.NetMessage.Send<SwiftlyS2.Shared.ProtobufDefinitions.CCSUsrMsg_Fade>(msg =>
        {
            msg.Duration = Math.Max(0, config.FadeDurationMs);
            msg.HoldTime = Math.Max(0, config.FadeHoldMs);
            msg.Flags = FadeFlagIn | FadeFlagPurge;
            msg.Clr = new Color(r, g, b, a);
            msg.Recipients.AddRecipient(player.Slot);
        });
    }

    /// <summary>Parses "R,G,B,A" (0-255 each) - returns false for anything malformed so callers can silently skip rather than throw.</summary>
    private static bool TryParseRgba(string value, out byte r, out byte g, out byte b, out byte a)
    {
        r = g = b = a = 0;
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 4 &&
            byte.TryParse(parts[0], out r) && byte.TryParse(parts[1], out g) &&
            byte.TryParse(parts[2], out b) && byte.TryParse(parts[3], out a);
    }

    /// <summary>Gauge-bar color band shared by every percentage-based gauge popup - green when healthy, orange mid, red low.</summary>
    public static string GetGaugeBarColor(float ratio) => ratio switch
    {
        > 0.5f => "lime",
        > 0.2f => "orange",
        _ => "red",
    };

    /// <summary>Modifier display name wrapped in [gold]...[default] and color-resolved - the shared accessibility formatting used by !rolllist and !rollactive.</summary>
    private static string GetColoredModifierDisplayName(ISwiftlyCore core, GameModifierBase modifier) =>
        SwiftlyS2.Shared.Helper.Colored($"[gold]{GetModifierDisplayName(core, modifier)}[default]");

    public static void PrintModifiersToChat(ISwiftlyCore core, SwiftlyS2.Shared.Players.IPlayer? player, IReadOnlyCollection<GameModifierBase> modifiers, string title, bool withDescriptions = true)
    {
        PrintTitleToChat(core, player, title);

        if (modifiers.Count == 0)
        {
            player?.SendChat("None");
            return;
        }

        foreach (var modifier in modifiers)
        {
            var displayName = GetColoredModifierDisplayName(core, modifier);
            // Bug fix: descriptions were sent raw while only the display name went through
            // Helper.Colored(), so color tokens inside a description printed literally in chat.
            var line = withDescriptions ? $"• {displayName} - [{GetModifierDescription(core, modifier)}]" : $"• {displayName}";
            player?.SendChat(SwiftlyS2.Shared.Helper.Colored(line));
        }
    }

    /// <summary>
    /// Resolves what a modifier's AssignedSlots means in player-facing terms: "Global" for an empty
    /// set (the same "empty means everyone" convention GameModifierBase.IsAssignedTo uses internally),
    /// or the connected player name(s) it's currently scoped to otherwise.
    /// </summary>
    private static string GetModifierScopeText(ISwiftlyCore core, GameModifierBase modifier)
    {
        if (modifier.AssignedSlots.Count == 0)
        {
            return "Global";
        }

        var names = modifier.AssignedSlots
            .Select(slot => core.PlayerManager.GetPlayer(slot)?.Controller is { IsValid: true } controller ? controller.PlayerName : $"Slot {slot}")
            .ToList();

        return string.Join(", ", names);
    }

    /// <summary>
    /// !rollactive's listing: unlike PrintModifiersToChat (used for the registered-modifier list,
    /// where scope is meaningless), each active modifier now also shows WHO it currently applies to -
    /// "Global" or specific player name(s) - since most modifiers are per-player-assigned by default
    /// (Config.RandomizePlayers), a flat name/description list with no scope was misleading.
    /// </summary>
    public static void PrintActiveModifiersToChat(ISwiftlyCore core, SwiftlyS2.Shared.Players.IPlayer? player, IReadOnlyCollection<GameModifierBase> modifiers)
    {
        PrintTitleToChat(core, player, "Active modifiers");

        if (modifiers.Count == 0)
        {
            player?.SendChat("None");
            return;
        }

        foreach (var modifier in modifiers)
        {
            var displayName = GetColoredModifierDisplayName(core, modifier);
            var scope = GetModifierScopeText(core, modifier);
            player?.SendChat($"• {displayName} ({scope}) - [{GetModifierDescription(core, modifier)}]");
        }
    }

    /// <summary>
    /// Looks up a translation key from resources/translations/en.jsonc via SwiftlyS2's own
    /// ILocalizer, returning false (rather than throwing or returning a placeholder) if there's no
    /// entry for it yet. It's unconfirmed whether a missing key returns the key itself, an empty
    /// string, or throws - all three are treated as "not found" here, so callers can fall back to
    /// their own hardcoded default.
    /// </summary>
    private static bool TryLocalize(ISwiftlyCore core, string key, out string value)
    {
        try
        {
            var translated = core.Localizer[key];
            if (!string.IsNullOrEmpty(translated) && translated != key)
            {
                value = translated;
                return true;
            }
        }
        catch
        {
            // No translation key for this yet - fall through to false below.
        }

        value = "";
        return false;
    }

    /// <summary>
    /// Looks up a modifier's chat-shown "explanation" text from resources/translations/en.jsonc
    /// (key: "{Name}.Description"), falling back to the modifier's hardcoded C# Description if no
    /// translation key exists for it yet - so admins can edit that one file to reword any modifier's
    /// description without touching code or rebuilding. Any "{token}" placeholder in the resolved
    /// text (translation OR hardcoded fallback) is then substituted using the modifier's
    /// DynamicTextTokens, if it has any - so a modifier with a live value (a chance rolled
    /// per-activation, a config-driven delay/timer, etc.) can still have its surrounding wording
    /// freely customized in the translation file while always showing the real current value(s).
    /// </summary>
    public static string GetModifierDescription(ISwiftlyCore core, GameModifierBase modifier)
    {
        var description = TryLocalize(core, $"{modifier.Name}.Description", out var value) ? value : modifier.Description;

        if (modifier.DynamicTextTokens is { } tokens)
        {
            foreach (var (token, replacement) in tokens)
            {
                description = description.Replace($"{{{token}}}", replacement);
            }
        }

        return description;
    }

    /// <summary>
    /// Looks up a modifier's chat/HTML-shown display name from resources/translations/en.jsonc (key:
    /// "{Name}.DisplayName"), falling back to the modifier's internal Name if no override exists.
    /// The internal Name itself is never renamed - it's still what IncompatibleModifiers lists,
    /// DisabledModifiers config, and admin commands (!rolltoggle etc.) all match against - this only
    /// changes what players actually see printed, e.g. renaming the confusing "BoomerangBullets"/"Butterfingers"
    /// pair to something clearer without touching any code or breaking existing references to them.
    /// </summary>
    public static string GetModifierDisplayName(ISwiftlyCore core, GameModifierBase modifier) =>
        TryLocalize(core, $"{modifier.Name}.DisplayName", out var value) ? value : modifier.Name;

    public static bool IsWarmupActive(ISwiftlyCore core)
    {
        var gameRules = core.EntitySystem.GetGameRules();
        return gameRules is { } rules && rules.WarmupPeriod;
    }

    // Deliberately no GetModifierName<T>() helper here anymore: it used to instantiate `new T()`
    // to read the Name off another modifier class for IncompatibleModifiers lists. Any pair of
    // modifiers referencing each other that way recurses forever at construction time (A's
    // ctor builds B to read its Name, B's ctor builds A to read its Name, ...) - this crashed
    // the server with a stack overflow the very first time two such modifiers were both
    // registered (Juggernaut <-> GlassCannon). Use plain string literals for cross-references
    // instead; a typo just means an incompatibility silently doesn't match, never a crash.

    /// <summary>
    /// Teleports a player, then temporarily disables their collision for one tick to avoid
    /// a mid-teleport stuck-in-geometry glitch - same safety hack the original CSS plugin used.
    ///
    /// Bug fix: this used to set CollisionGroup.Dissolving for that one-tick window - per the SDK's
    /// own enum doc comment, that group means "things that are dissolving" (a prop fade-out state),
    /// not "player temporarily passing through geometry". Reported symptom (Revive specifically:
    /// clipping through walls persisting well after the revive, not just for one frame) points at
    /// this group's collision behavior not being a clean, reliably-restorable "nonsolid" state for a
    /// live player pawn. Switched to CollisionGroup.Pushaway, whose own doc comment is literally
    /// "Nonsolid on client and server, pushaway in player code" - the group actually intended for
    /// this. Also loosened the restore below from requiring IsAlive to just IsValid, so a revived
    /// player who's technically mid-respawn-transition at the exact restore tick still gets their
    /// collision group put back rather than being silently left stuck non-solid.
    /// </summary>
    public static void TeleportPlayer(ISwiftlyCore core, IPlayer player, Vector position, QAngle? angle = null, Vector? velocity = null)
    {
        if (player.PlayerPawn is not { } pawn)
        {
            return;
        }

        // Bug fix: this used to pass the full EyeAngles (pitch and all, whether defaulted from the
        // pawn or passed in explicitly - Flanker passes the FLANK TARGET's EyeAngles) straight
        // into Teleport()'s angle parameter. A player's entity rotation only ever has a meaningful
        // YAW - pitch (looking up/down) is a client-side camera value, never meant to be baked into
        // the body's absolute orientation. Teleporting/reviving while looking at the ground visibly
        // tilted the whole body model by that pitch, which in turn threw off movement direction
        // ("walking weird") and the collision hull's assumption of "upright" (occasional
        // see-through-wall glitches). Only yaw ever reaches Teleport(); pitch/roll are always zeroed.
        var facingYaw = (angle ?? pawn.EyeAngles).Yaw;
        player.Teleport(position, new QAngle(0f, facingYaw, 0f), velocity ?? new Vector(0, 0, 0));

        pawn.Collision.CollisionGroup = (byte)CollisionGroup.Pushaway;
        pawn.Collision.CollisionGroupUpdated();

        var slot = player.Slot;
        core.Scheduler.NextWorldUpdate(() =>
        {
            if (core.PlayerManager.GetPlayer(slot) is { IsValid: true } current &&
                current.PlayerPawn is { } currentPawn)
            {
                currentPawn.Collision.CollisionGroup = (byte)CollisionGroup.Player;
                currentPawn.Collision.CollisionGroupUpdated();
            }
        });
    }

    public static void SwapPlayerLocations(ISwiftlyCore core, IPlayer first, IPlayer second)
    {
        if (first.PlayerPawn?.AbsOrigin is not { } firstPosition || second.PlayerPawn?.AbsOrigin is not { } secondPosition)
        {
            return;
        }

        TeleportPlayer(core, first, secondPosition);
        TeleportPlayer(core, second, firstPosition);
    }

    /// <summary>
    /// Resolves a native entity handle (e.g. CTakeDamageInfo.Attacker, which is a raw
    /// CHandle&lt;CEntityInstance&gt; - CTakeDamageInfo has no AttackerPlayer/AttackerPawn
    /// convenience field, unlike the game-event types) to the owning IPlayer, or null if the
    /// handle is empty/invalid or doesn't resolve to a player pawn (e.g. world/NPC damage).
    /// </summary>
    public static IPlayer? GetPlayerFromEntityHandle(ISwiftlyCore core, CHandle<CEntityInstance> handle)
    {
        if (handle.Value is not { } entity)
        {
            return null;
        }

        var player = core.PlayerManager.GetPlayerFromPawn(entity.As<CBasePlayerPawn>());
        return player is { IsValid: true } ? player : null;
    }

    /// <summary>
    /// Identity check for "is this the same connected player" (self-damage/self-kill exclusion).
    /// Bug fix: several modifiers used to compare SteamID for this - bot SteamID is fixed at 0 for
    /// every bot (confirmed via the SDK's own IPlayer.SteamID doc comment), so any bot-vs-different-bot
    /// interaction was misread as "hit themselves" and silently excluded. Slot is unique per connected
    /// player (human or bot) and is what every other per-player lookup in this codebase already keys
    /// on, so it doesn't have that collision.
    /// </summary>
    public static bool IsSamePlayer(IPlayer a, IPlayer b) => a.Slot == b.Slot;

    /// <summary>Resolves a thrown grenade projectile's owning player, or null if it's already gone.</summary>
    public static IPlayer? GetThrowerPlayer(ISwiftlyCore core, CBaseCSGrenadeProjectile grenade)
    {
        return grenade.Thrower.Value is { } pawn ? core.PlayerManager.GetPlayerFromPawn(pawn) : null;
    }

    public static Vector? GetSpawnLocation(ISwiftlyCore core, Team team)
    {
        if (team == Team.T)
        {
            var spawns = core.EntitySystem.GetAllEntitiesByDesignerName<CInfoPlayerTerrorist>("info_player_terrorist").ToList();
            return spawns.Count > 0 ? spawns[Random.Shared.Next(spawns.Count)].AbsOrigin : null;
        }

        if (team == Team.CT)
        {
            var spawns = core.EntitySystem.GetAllEntitiesByDesignerName<CInfoPlayerCounterterrorist>("info_player_counterterrorist").ToList();
            return spawns.Count > 0 ? spawns[Random.Shared.Next(spawns.Count)].AbsOrigin : null;
        }

        return null;
    }
}
