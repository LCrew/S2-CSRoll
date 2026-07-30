using CSRoll.Modifiers;

namespace CSRoll;

/// <summary>
/// Explicit factory list for every hardcoded modifier, replacing CSS's assembly-reflection
/// auto-discovery (CSRollUtils.GetAllChildClasses&lt;GameModifierBase&gt;()).
/// ConVarModifiers/*.cfg-driven modifiers are NOT listed here - they're discovered from disk
/// by ModifierRuntime.InitialiseCvarModifiers().
///
/// Instance method (not static) because the Phase 1g NavMesh-dependent factories need to
/// capture _navMeshService, which only exists once the plugin has loaded.
/// </summary>
public partial class CSRoll
{
    private List<Func<GameModifierBase>> BuildModifierFactories() =>
    [
        // Phase 1b
        () => new GameModifierJuggernaut(),
        () => new GameModifierRandomHealth(),
        () => new GameModifierSpeedhack(),
        () => new GameModifierSmallPlayers(),
        () => new GameModifierVampire(),
        () => new GameModifierDrunk(),

        // Phase 1c
        () => new GameModifierMoreDamage(),
        () => new GameModifierOnePerMag(),
        () => new GameModifierOneInTheChamber(),
        () => new GameModifierNoSpread(),
        () => new GameModifierDropOnMiss(),
        () => new GameModifierDontMiss(),
        () => new GameModifierKnifeOnly(),
        () => new GameModifierRandomWeapon(),
        () => new GameModifierRandomWeapons(),
        () => new GameModifierGrenadesOnly(),
        () => new GameModifierLongerFlashes(),
        () => new GameModifierRandomGrenadeTime(),
        () => new GameModifierRainbowSmokes(),

        // Phase 1d
        () => new GameModifierXrayAll(),

        // Phase 1f
        () => new GameModifierSwapPlacesOnKill(),
        () => new GameModifierSwapPlacesOnHit(),
        () => new GameModifierResetOnReload(),

        // Phase 1g
        () => new GameModifierTeleportOnReload(_navMeshService),
        () => new GameModifierTeleportOnHit(_navMeshService),

        // New modifiers: cluster grenades, master zeus, hardhead/ironbody, poison smoke/smoke immunity
        () => new GameModifierClusterGrenades(),
        () => new GameModifierMasterZeus(),
        () => new GameModifierHardHead(),
        () => new GameModifierIronBody(),
        () => new GameModifierPoisonSmoke(),
        () => new GameModifierSmokeImmunity(),

        // New modifiers: conditional/full invisibility, speedhack, flashing bullets, revive, saint
        () => new GameModifierConditionalInvisibility(),
        () => new GameModifierFullInvisibility(),
        () => new GameModifierFlashingBullets(),
        () => new GameModifierRevive(),
        () => new GameModifierSaint(),

        // Bug fix: LeadBoots/HighGravity/LowGravity/SuperJump/InfiniteAmmo/BiggerExplosions/
        // IncreasedSpread used to be resources/ConVarModifiers/*.cfg entries driving server-wide
        // cvars (sv_maxspeed, sv_gravity, sv_jump_impulse, sv_infinite_ammo,
        // sv_hegrenade_damage_multiplier, weapon_accuracy_forcespread) - applied to the whole server
        // instead of just whoever rolled them. Rewritten as proper per-player C# modifiers.
        // (Bhop was also converted this way, then removed entirely: the landing-penalty removal
        // half worked, but the auto-jump-without-repressing half wasn't achievable without risking
        // corrupting CS2's native jump physics - see git history if revisiting this.)
        () => new GameModifierLeadBoots(),
        () => new GameModifierHighGravity(),
        () => new GameModifierLowGravity(),
        () => new GameModifierSuperJump(),
        () => new GameModifierInfiniteAmmo(),
        () => new GameModifierBiggerExplosions(),
        () => new GameModifierIncreasedSpread(),
        () => new GameModifierDisarmingBullets(),

        // Bug fix/redesign: PlantAnywhere used to be a resources/ConVarModifiers/*.cfg entry with no
        // timing control (mp_plant_c4_anywhere on for the whole round, no bomb-timer change).
        () => new GameModifierPlantAnywhere(),

        () => new GameModifierKamikaze(),
    ];
}
