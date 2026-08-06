using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Bug fix: this used to be resources/ConVarModifiers/BiggerExplosionsModifier.cfg, driving
/// sv_hegrenade_damage_multiplier/sv_hegrenade_radius_multiplier - both server-wide, so every
/// player's HE grenades hit harder and wider instead of just whoever rolled it. Rewritten per-player
/// via Core.GameHooks.Entities.TakeDamage.Pre (the same hook HardHead/IronBody/Revive/Jetpack use
/// for other per-player damage exceptions): DMG_BLAST damage dealt by an assigned player's HE gets
/// multiplied.
///
/// The radius half of the original effect is NOT replicated: the native engine has already decided
/// who's within blast range and applies its own falloff before TakeDamage.Pre ever fires per victim
/// - anyone outside the grenade's real native radius never generates a damage event here at all, so
/// there's nothing to intercept for them. Actually extending the radius per-player would mean fully
/// reimplementing grenade detonation (manual nearby-player search + custom falloff math, applied
/// only to players the native explosion didn't already reach) rather than adjusting an existing
/// damage number - out of scope for this pass. Bigger damage on an unchanged radius is what's here.
/// </summary>
public sealed class GameModifierBiggerExplosions : GameModifierBase
{
    public GameModifierBiggerExplosions()
    {
        Name = "BiggerExplosions";
        Description = "HE Grenades deal much more damage";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnEnabled()
    {
        Core.GameHooks.Entities.TakeDamage.Pre += OnTakeDamage;
    }

    protected override void OnDisabled()
    {
        Core.GameHooks.Entities.TakeDamage.Pre -= OnTakeDamage;
    }

    private void OnTakeDamage(ref TakeDamageEntityPreContext ctx)
    {
        if ((ctx.Params.Info.DamageType & DamageTypes_t.DMG_BLAST) == 0)
        {
            return;
        }

        var attacker = CSRollUtils.GetPlayerFromEntityHandle(Core, ctx.Params.Info.Attacker);
        if (attacker is not { IsValid: true } || !IsAssignedTo(attacker.Slot))
        {
            return;
        }

        ctx.Params.Info.Damage *= Runtime.Config.BiggerExplosions.DamageMultiplier;
    }
}
