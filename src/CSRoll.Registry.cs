using CSRoll.Modifiers;

namespace CSRoll;

/// <summary>
/// Explicit factory list for every hardcoded modifier, replacing CSS's assembly-reflection
/// auto-discovery (CSRollUtils.GetAllChildClasses&lt;GameModifierBase&gt;()).
/// ConVarModifiers/*.cfg-driven modifiers are NOT listed here - they're discovered from disk
/// by ModifierRuntime.InitialiseCvarModifiers().
///
/// Kept as an instance method (not static) for consistency with the rest of this plugin's
/// lifecycle-scoped setup, even though no current factory below actually needs to capture instance
/// state - TeleportOnReload/TeleportOnHit (Phase 1g) used to need _navMeshService, but no longer
/// depend on NavMesh at all (see GameModifierTeleportNavMesh.cs).
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
        // OneInTheChamber removed per explicit request (too close to OnePerReload / broke game feel).
        () => new GameModifierMoreDamage(),
        () => new GameModifierOnePerMag(),
        () => new GameModifierNoRecoil(),
        () => new GameModifierDropOnMiss(),
        () => new GameModifierDontMiss(),
        () => new GameModifierRandomLoadout(),
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
        // Bug fix: no longer NavMesh-dependent - always teleports to the player's own team spawn now
        // (a random NavMesh/any-team position could break Wingman maps by landing in the enemy spawn).
        () => new GameModifierTeleportOnReload(),
        () => new GameModifierTeleportOnHit(),

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

        // Bug fix: LeadBoots/Jetpack/Bhop/InfiniteAmmo/BiggerExplosions/IncreasedSpread used to be
        // resources/ConVarModifiers/*.cfg entries driving server-wide cvars (sv_maxspeed,
        // sv_jump_impulse, sv_infinite_ammo, sv_hegrenade_damage_multiplier,
        // weapon_accuracy_forcespread) - applied to the whole server instead of just whoever rolled
        // them. Rewritten as proper per-player C# modifiers.
        // (Bhop was originally removed entirely after its auto-jump-without-repressing half proved
        // unachievable via ProcessMovement.Pre velocity injection without risking corrupting CS2's
        // native jump physics - then reinstated via a different hook point once one was found (see
        // GameModifierBhop.cs's own class doc comment). HighGravity/LowGravity went a similar route -
        // GravityScale writes never actually affected physics live, see git history - but were removed
        // entirely per explicit request rather than left broken, with no working alternative found.
        // Jetpack's hold-to-thrust mechanic was likewise abandoned back to plain SuperJump after
        // failing several times, then reinstated once a working technique was found (see
        // GameModifierJetpack.cs's own class doc comment for the full history).)
        () => new GameModifierLeadBoots(),
        () => new GameModifierJetpack(),
        () => new GameModifierBhop(),
        () => new GameModifierInfiniteAmmo(),
        () => new GameModifierBiggerExplosions(),
        () => new GameModifierIncreasedSpread(),
        () => new GameModifierDisarmingBullets(),

        // Bug fix/redesign: PlantAnywhere used to be a resources/ConVarModifiers/*.cfg entry with no
        // timing control (mp_plant_c4_anywhere on for the whole round, no bomb-timer change).
        () => new GameModifierPlantAnywhere(),

        () => new GameModifierKamikaze(),

        // New: FlankTeleport (Inspect-Weapon-triggered teleport behind a random enemy, on a
        // cooldown - a from-scratch modifier, not a NavMesh-dependent bug fix).
        () => new GameModifierFlankTeleport(),
    ];
}
