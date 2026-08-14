using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace CSRoll.Modifiers;

/// <summary>
/// Blocks headshot damage entirely - body damage only. Uses CTakeDamageInfo.ActualHitGroup rather
/// than the DMG_HEADSHOT DamageType bit: live testing showed the DamageType classification flags
/// (headshot/blast/burn) aren't reliably populated yet at the point TakeDamage.Pre fires - a
/// headshot's DMG_HEADSHOT bit never showed up, so the check never blocked anything. ActualHitGroup
/// is a computed property (unlike the raw HitGroupId field, documented as holding a garbage value
/// in-game - "Use ActualHitGroup instead") and reliably reflects which hitbox was actually hit.
/// </summary>
public sealed class GameModifierHardHead : GameModifierBase
{
    public GameModifierHardHead()
    {
        Name = "HardHead";
        Description = "Cannot be damaged by headshots - body damage only";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = ["IronBody"];
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
        if (!TryGetAssignedTakeDamageVictim(ref ctx, out _))
        {
            return;
        }

        if (ctx.Params.Info.ActualHitGroup == HitGroup_t.HITGROUP_HEAD)
        {
            ctx.Params.Info.Damage = 0;
        }
    }
}
