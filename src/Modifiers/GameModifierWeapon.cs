using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// 1 bullet per magazine - the assigned player's weapon is kept at exactly 1 round in the clip,
/// reserve ammo kept topped up so reloading never actually runs dry.
///
/// Bug fix: this used to shrink CBasePlayerWeaponVData.MaxClip1, a field shared by the weapon TYPE
/// across every player holding one (not a per-instance field) - and did so unconditionally in
/// OnEnabled/OnDisabled/OnItemEquip/OnWeaponReload with no IsAssignedTo check anywhere at all, so
/// activating this modifier for one player capped EVERY connected player's matching weapons to 1
/// bullet, and would have silently ignored !memodifier's "just you" scoping entirely too.
///
/// Bug fix 2: the next rewrite force-set Clip1=1 and topped up reserve ammo, both directly inside the
/// OnWeaponReload event handler - but that turned out to depend on exactly when that event fires
/// relative to the engine's own native ammo transfer, and got it wrong in two different directions on
/// two different attempts (first: reload gave back a full magazine, because the transfer ran AFTER
/// the event and drew from the reserve we'd just topped up; second attempt at fixing that moved
/// Clip1 to a per-tick clamp but left the reserve top-up event-driven, which could still leave
/// reserve at 0 - "can't reload anymore" - if the transfer instead ran BEFORE the event, or under
/// some other ordering this hadn't accounted for). Rather than guess a third time at the exact event
/// ordering, both Clip1 and reserve are now enforced unconditionally every tick, independent of the
/// reload event entirely: Clip1 is clamped down to at most 1, and reserve is topped back up to max
/// whenever it's below max. Neither can drift away from "exactly 1 in the clip, full reserve always"
/// for more than a tick, regardless of what order the native reload logic does its own ammo math in.
/// </summary>
public sealed class GameModifierOnePerMag : GameModifierBase
{
    public GameModifierOnePerMag()
    {
        Name = "OnePerReload";
        Description = "1 bullet per reload";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = ["InfiniteAmmo"];
    }

    protected override void OnEnabled()
    {
        Core.Event.OnTick += EnforceOnePerMag;
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= EnforceOnePerMag;
    }

    private void EnforceOnePerMag()
    {
        foreach (var player in GetAssignedPlayers())
        {
            if (player.PlayerPawn?.WeaponServices?.ActiveWeapon.Value is not { } weapon)
            {
                continue;
            }

            if (weapon.Clip1 > 1)
            {
                weapon.Clip1 = 1;
                weapon.Clip1Updated();
            }

            if (weapon.PlayerWeaponVData?.As<CCSWeaponBaseVData>() is { } vData && weapon.ReserveAmmo[0] < vData.PrimaryReserveAmmoMax)
            {
                weapon.ReserveAmmo[0] = vData.PrimaryReserveAmmoMax;
                weapon.ReserveAmmoUpdated();
            }
        }
    }
}

/// <summary>
/// Cancels aim punch (recoil kick-back) every tick for perfect aim, for whatever weapon is
/// currently held - not tied to any single weapon type.
///
/// Bug fix: this also used to have a bolt-on ModifierConfig/NoSpread.cfg setting the server-wide
/// weapon_accuracy_nospread cvar to true - completely redundant with (and worse than) the per-player
/// aim-punch cancellation below, since it silently gave every connected player perfect accuracy the
/// instant this modifier activated for anyone, not just the assigned player. Deleted.
///
/// Renamed from NoSpread to NoRecoil (more accurate name - this cancels the recoil kick-back, not
/// bullet spread) and switched from an EventWeaponFire hook (reset once, right after each shot) to
/// a Core.Event.OnTick loop that continuously zeroes aim punch for every assigned+alive player every
/// tick, regardless of which weapon they're holding - more thorough than resetting only at the
/// instant of firing, and doesn't need any per-weapon-type branching since AimPunchServices lives on
/// the player pawn, not the weapon. Also now clears UnpredictableBaseAngle alongside the three
/// Predictable* fields the old version reset - the full set CCSPlayer_AimPunchServices exposes.
/// </summary>
public sealed class GameModifierNoRecoil : GameModifierBase
{
    public GameModifierNoRecoil()
    {
        Name = "NoRecoil";
        Description = "Weapons have no recoil";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnEnabled()
    {
        Core.Event.OnTick += CancelAimPunch;
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= CancelAimPunch;
    }

    private void CancelAimPunch()
    {
        foreach (var player in Core.PlayerManager.GetAlive())
        {
            if (!IsAssignedTo(player.Slot) || player.PlayerPawn?.AimPunchServices is not { } aimPunch)
            {
                continue;
            }

            aimPunch.PredictableBaseAngle = default;
            aimPunch.PredictableBaseAngleUpdated();
            aimPunch.PredictableBaseAngleVel = default;
            aimPunch.PredictableBaseAngleVelUpdated();
            aimPunch.PredictableBaseTickInterpAmount = 0f;
            aimPunch.PredictableBaseTickInterpAmountUpdated();
            aimPunch.UnpredictableBaseAngle = default;
            aimPunch.UnpredictableBaseAngleUpdated();
        }
    }
}
