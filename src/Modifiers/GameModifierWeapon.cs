using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
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
        IncompatibleModifiers = ["OneInTheChamber", "InfiniteAmmo"];
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
        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (!IsAssignedTo(player.Slot) || player.PlayerPawn?.WeaponServices?.ActiveWeapon.Value is not { } weapon)
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
/// 1 bullet per kill - every weapon the assigned player ever holds is limited to a single chambered
/// round, hugely amplified damage compensates for the near-empty clip.
///
/// Bug fix: the 1-bullet restriction used to be applied only once, to whatever weapons the player
/// already held at the moment this modifier activated - a weapon bought or picked up afterwards kept
/// its normal full clip/reserve untouched. Ammo was also fully drained to 0 reserve with no way to
/// top back up except landing a hit (which grants +1 reserve) - miss your one shot and you were
/// stuck with a permanently empty weapon for the rest of your life, unable to ever fire again. Fixed
/// per explicit request: an EventItemEquip hook now re-applies the 1-bullet clip to every weapon the
/// player touches (buy, pickup, or switch), and an EventWeaponReload hook always tops the clip back
/// up to 1 regardless of reserve ammo - "unlimited magazines" - so reloading after a miss always
/// works instead of only ever gaining ammo back via a landed hit.
/// </summary>
public sealed class GameModifierOneInTheChamber : GameModifierBase
{
    private const float DamageMultiplier = 10.0f;
    private Guid _equipHookId;
    private Guid _reloadHookId;

    public GameModifierOneInTheChamber()
    {
        Name = "OneInTheChamber";
        Description = "1 bullet per kill";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = ["OnePerReload", "InfiniteAmmo"];
    }

    protected override void OnEnabled()
    {
        Core.GameHooks.Entities.TakeDamage.Pre += OnTakeDamage;
        _equipHookId = Core.GameEvent.HookPost<EventItemEquip>(OnItemEquip);
        _reloadHookId = Core.GameEvent.HookPost<EventWeaponReload>(OnWeaponReload);

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (!IsAssignedTo(player.Slot) || player.PlayerPawn?.WeaponServices is not { } weaponServices)
            {
                continue;
            }

            foreach (var weapon in weaponServices.MyValidWeapons)
            {
                ApplyWeaponModifier(weapon);
            }
        }
    }

    protected override void OnDisabled()
    {
        Core.GameHooks.Entities.TakeDamage.Pre -= OnTakeDamage;
        Core.GameEvent.Unhook(_equipHookId);
        Core.GameEvent.Unhook(_reloadHookId);
    }

    private void OnTakeDamage(ref TakeDamageEntityPreContext ctx)
    {
        if (!IsAssignedTo(CSRollUtils.GetPlayerFromEntityHandle(Core, ctx.Params.Info.Attacker)?.Slot ?? -1))
        {
            return;
        }

        ctx.Params.Info.Damage *= DamageMultiplier;
    }

    private HookResult OnItemEquip(EventItemEquip @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot) &&
            player.PlayerPawn?.WeaponServices?.ActiveWeapon.Value is { } weapon)
        {
            ApplyWeaponModifier(weapon);
        }

        return HookResult.Continue;
    }

    private HookResult OnWeaponReload(EventWeaponReload @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot) &&
            player.PlayerPawn?.WeaponServices?.ActiveWeapon.Value is { } weapon)
        {
            // "Unlimited magazines": every reload tops the chamber back up to exactly 1 round
            // regardless of reserve ammo, rather than draining reserve to 0 and leaving the player
            // with no way to ever reload again after their one shot.
            ApplyWeaponModifier(weapon);
        }

        return HookResult.Continue;
    }

    private static void ApplyWeaponModifier(CBasePlayerWeapon weapon)
    {
        weapon.Clip1 = 1;
        weapon.Clip1Updated();
        weapon.Clip2 = 0;
        weapon.Clip2Updated();
        weapon.ReserveAmmo[0] = 0;
        weapon.ReserveAmmoUpdated();
    }
}

/// <summary>
/// Cancels aim punch (recoil kick-back) on every shot for perfect aim.
///
/// Bug fix: this also used to have a bolt-on ModifierConfig/NoSpread.cfg setting the server-wide
/// weapon_accuracy_nospread cvar to true - completely redundant with (and worse than) the per-player
/// aim-punch cancellation below, since it silently gave every connected player perfect accuracy the
/// instant this modifier activated for anyone, not just the assigned player. Deleted; the hook below
/// was already sufficient on its own.
/// </summary>
public sealed class GameModifierNoSpread : GameModifierBase
{
    private Guid _fireHookId;

    public GameModifierNoSpread()
    {
        Name = "NoSpread";
        Description = "Weapons have perfect aim";
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
        if (@event.UserIdPlayer is not { IsValid: true } player || !IsAssignedTo(player.Slot) ||
            @event.UserIdPawn?.AimPunchServices is not { } aimPunch)
        {
            return HookResult.Continue;
        }

        aimPunch.PredictableBaseAngle = default;
        aimPunch.PredictableBaseAngleVelUpdated();
        aimPunch.PredictableBaseAngleVel = default;
        aimPunch.PredictableBaseAngleUpdated();
        aimPunch.PredictableBaseTickInterpAmount = 0f;
        aimPunch.PredictableBaseTickInterpAmountUpdated();

        return HookResult.Continue;
    }
}
