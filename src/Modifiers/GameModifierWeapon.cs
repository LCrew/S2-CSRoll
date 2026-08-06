using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>1 bullet per magazine - shrinks the weapon TYPE's clip size (shared VData, affects everyone using that weapon).</summary>
public sealed class GameModifierOnePerMag : GameModifierBase
{
    private readonly Dictionary<string, int> _cachedMaxClip1 = [];
    private Guid _equipHookId;
    private Guid _reloadHookId;

    public GameModifierOnePerMag()
    {
        Name = "OnePerReload";
        Description = "1 bullet per reload";
        SupportsRandomRounds = true;
        IncompatibleModifiers = ["OneInTheChamber", "InfiniteAmmo"];
    }

    protected override void OnEnabled()
    {
        _equipHookId = Core.GameEvent.HookPost<EventItemEquip>(OnItemEquip);
        // 1-bullet clips would mean constantly running dry without this - top reserve ammo
        // back up to max on every reload so the only limiter is the 1-bullet clip itself.
        _reloadHookId = Core.GameEvent.HookPost<EventWeaponReload>(OnWeaponReload);

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (player.PlayerPawn?.WeaponServices is not { } weaponServices)
            {
                continue;
            }

            foreach (var weapon in weaponServices.MyValidWeapons)
            {
                ApplyToWeapon(weapon);
            }
        }
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_equipHookId);
        Core.GameEvent.Unhook(_reloadHookId);

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (player.PlayerPawn?.WeaponServices is not { } weaponServices)
            {
                continue;
            }

            foreach (var weapon in weaponServices.MyValidWeapons)
            {
                RemoveFromWeapon(weapon);
            }
        }

        _cachedMaxClip1.Clear();
    }

    private HookResult OnItemEquip(EventItemEquip @event)
    {
        if (@event.UserIdPlayer?.PlayerPawn?.WeaponServices?.ActiveWeapon.Value is { } weapon)
        {
            ApplyToWeapon(weapon);
        }

        return HookResult.Continue;
    }

    private HookResult OnWeaponReload(EventWeaponReload @event)
    {
        if (@event.UserIdPlayer?.PlayerPawn?.WeaponServices?.ActiveWeapon.Value is { } weapon &&
            weapon.PlayerWeaponVData?.As<CCSWeaponBaseVData>() is { } vData)
        {
            weapon.ReserveAmmo[0] = vData.PrimaryReserveAmmoMax;
            weapon.ReserveAmmoUpdated();
        }

        return HookResult.Continue;
    }

    private void ApplyToWeapon(CBasePlayerWeapon weapon)
    {
        var vData = weapon.PlayerWeaponVData;
        if (vData is null)
        {
            return;
        }

        _cachedMaxClip1.TryAdd(weapon.DesignerName, vData.MaxClip1);
        vData.MaxClip1 = 1;
        weapon.Clip1 = 1;
        weapon.Clip1Updated();
    }

    private void RemoveFromWeapon(CBasePlayerWeapon weapon)
    {
        var vData = weapon.PlayerWeaponVData;
        if (vData is null)
        {
            return;
        }

        if (_cachedMaxClip1.TryGetValue(weapon.DesignerName, out var original))
        {
            vData.MaxClip1 = original;
        }

        weapon.Clip1 = Math.Min(weapon.Clip1, vData.MaxClip1);
        weapon.Clip1Updated();
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

/// <summary>Cancels aim punch (recoil kick-back) on every shot for perfect aim.</summary>
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
