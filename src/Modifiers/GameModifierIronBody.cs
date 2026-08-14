using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace CSRoll.Modifiers;

/// <summary>
/// Only takes damage from headshots or utility (HE/molotov/incendiary) - all other body damage is
/// blocked. Inverse of HardHead, and mutually incompatible with it for the same reason.
///
/// Uses ActualHitGroup for the headshot check and the Inflictor entity's DesignerName for the
/// utility check, rather than DamageType bits: live testing showed DamageType's classification
/// flags (DMG_HEADSHOT/DMG_BLAST/DMG_BURN) aren't reliably populated yet at the point
/// TakeDamage.Pre fires - the AllowedDamageTypes bitmask never matched anything, so every hit
/// (headshots and molotovs included) fell through to the "block it" branch. ActualHitGroup and
/// designer-name checks (the same technique GameModifierRandomGrenadeTime/ClusterGrenades/
/// RainbowSmokes already use successfully) sidestep DamageType entirely.
/// </summary>
public sealed class GameModifierIronBody : GameModifierBase
{
    private static readonly HashSet<string> AllowedInflictorDesignerNames =
    [
        "hegrenade_projectile", "molotov_projectile", "incgrenade_projectile", "inferno",
    ];

    public GameModifierIronBody()
    {
        Name = "IronBody";
        Description = "Can only be damaged by headshots or utility (HE/molotov)";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = ["HardHead"];
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

        var isHeadshot = ctx.Params.Info.ActualHitGroup == HitGroup_t.HITGROUP_HEAD;
        var isUtility = ctx.Params.Info.Inflictor.Value?.DesignerName is { } designerName &&
            AllowedInflictorDesignerNames.Contains(designerName);

        if (!isHeadshot && !isUtility)
        {
            ctx.Params.Info.Damage = 0;
        }
    }
}
