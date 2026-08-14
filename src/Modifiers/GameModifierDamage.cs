using SwiftlyS2.Shared.GameHooks;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Scales all entity damage. Replaces CSS's raw virtual-function hook
/// (VirtualFunctions.CBaseEntity_TakeDamageOldFunc) with SwiftlyS2's native
/// GameHooks.Entities.TakeDamage hook - no low-level memory/vtable hooking needed. Matches the
/// original's broad scope (affects breakables/NPCs too, not just player-vs-player damage).
/// </summary>
public abstract class GameModifierDamageMultiplier : GameModifierBase
{
    protected abstract float GetDamageMultiplier();

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
        // Bug fix: GetPlayerFromEntityHandle returns null for any non-player attacker (world/fall
        // damage, NPCs, self-inflicted environmental damage). Coercing that to slot -1 used to make
        // IsAssignedTo(-1) return true whenever this modifier was active in global scope (empty
        // AssignedSlots means "everyone"), so e.g. fall damage got multiplied by GetDamageMultiplier()
        // too - not just damage actually dealt by an assigned player, which is the stated intent.
        // Bailing out here whenever there's no resolvable attacker keeps that scoped correctly.
        var attacker = CSRollUtils.GetPlayerFromEntityHandle(Core, ctx.Params.Info.Attacker);
        if (attacker is null || !IsAssignedTo(attacker.Slot))
        {
            return;
        }

        ctx.Params.Info.Damage *= GetDamageMultiplier();
    }
}

public sealed class GameModifierMoreDamage : GameModifierDamageMultiplier
{
    public GameModifierMoreDamage()
    {
        Name = "MoreDamage";
        Description = "Damage dealt is increased by 33%";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override float GetDamageMultiplier() => 1.33f;
}
