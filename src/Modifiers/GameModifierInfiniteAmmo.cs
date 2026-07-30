using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace CSRoll.Modifiers;

/// <summary>
/// Bug fix: this used to be resources/ConVarModifiers/InfiniteAmmoModifier.cfg, driving
/// sv_infinite_ammo (plus sv_cheats) - both server-wide, so it gave every player infinite ammo
/// instead of just whoever rolled it. Rewritten per-player: on EventWeaponFire for an assigned
/// shooter (the same event MasterZeus already hooks), the just-fired weapon's Clip1 and reserve
/// ammo are topped straight back up via its own CBasePlayerWeaponVData.MaxClip1/
/// CCSWeaponBaseVData.PrimaryReserveAmmoMax - both confirmed per-weapon-instance schema fields, not
/// cvars, so this only ever touches the assigned player's own weapon.
/// </summary>
public sealed class GameModifierInfiniteAmmo : GameModifierBase
{
    private Guid _fireHookId;

    public GameModifierInfiniteAmmo()
    {
        Name = "InfiniteAmmo";
        Description = "All weapons have infinite ammo";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnEnabled()
    {
        _fireHookId = Core.GameEvent.HookPost<EventWeaponFire>(OnWeaponFire);
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_fireHookId);
    }

    private HookResult OnWeaponFire(EventWeaponFire @event)
    {
        if (@event.UserIdPlayer is not { IsValid: true } shooter || !IsAssignedTo(shooter.Slot) ||
            @event.UserIdPawn?.WeaponServices?.ActiveWeapon.Value is not { } weapon)
        {
            return HookResult.Continue;
        }

        if (weapon.PlayerWeaponVData is { } vData)
        {
            weapon.Clip1 = vData.MaxClip1;
            weapon.Clip1Updated();
        }

        if (weapon.PlayerWeaponVData?.As<CCSWeaponBaseVData>() is { } csVData)
        {
            weapon.ReserveAmmo[0] = csVData.PrimaryReserveAmmoMax;
            weapon.ReserveAmmoUpdated();
        }

        return HookResult.Continue;
    }
}
