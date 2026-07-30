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
        if (!IsAssignedTo(CSRollUtils.GetPlayerFromEntityHandle(Core, ctx.Params.Info.Attacker)?.Slot ?? -1))
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
        Description = "Damage dealt is doubled";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override float GetDamageMultiplier() => 2.0f;
}
