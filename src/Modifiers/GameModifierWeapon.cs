using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// 1 bullet per magazine - the assigned player's weapon is kept at exactly 1 round in the clip.
///
/// Bug fix: this used to shrink CBasePlayerWeaponVData.MaxClip1, a field shared by the weapon TYPE
/// across every player holding one (not a per-instance field) - and did so unconditionally in
/// OnEnabled/OnDisabled/OnItemEquip/OnWeaponReload with no IsAssignedTo check anywhere at all, so
/// activating this modifier for one player capped EVERY connected player's matching weapons to 1
/// bullet, and would have silently ignored !memodifier's "just you" scoping entirely too.
///
/// Bug fix 2: the first per-player rewrite force-set Clip1=1 directly inside the OnWeaponReload
/// event handler - but that event fires when the reload is TRIGGERED, before the engine has actually
/// moved any ammo from reserve into the clip, not after. Since reserve ammo is also topped up to max
/// in that same handler, the native reload completion (which runs some time after this event, once
/// the reload animation/timing finishes) then had a full reserve to draw from and filled the clip
/// back up past 1 - "gets a full mag after reloading" was the direct symptom. Fixed by clamping
/// Clip1 down to 1 every tick instead of reacting to the reload event's timing at all: this is
/// idempotent, catches the native fill within at most one tick regardless of exactly when it
/// happens, and needs no assumption about event ordering.
/// </summary>
public sealed class GameModifierOnePerMag : GameModifierBase
{
    private Guid _reloadHookId;

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
        Core.Event.OnTick += ClampClipToOne;
        // 1-bullet clips would mean constantly running dry without this - top reserve ammo
        // back up to max on every reload so the only limiter is the 1-bullet clip itself.
        _reloadHookId = Core.GameEvent.HookPost<EventWeaponReload>(OnWeaponReload);
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= ClampClipToOne;
        Core.GameEvent.Unhook(_reloadHookId);
    }

    private void ClampClipToOne()
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
        }
    }

    private HookResult OnWeaponReload(EventWeaponReload @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot) &&
            player.PlayerPawn?.WeaponServices?.ActiveWeapon.Value is { } weapon &&
            weapon.PlayerWeaponVData?.As<CCSWeaponBaseVData>() is { } vData)
        {
            weapon.ReserveAmmo[0] = vData.PrimaryReserveAmmoMax;
            weapon.ReserveAmmoUpdated();
        }

        return HookResult.Continue;
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
