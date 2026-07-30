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

/// <summary>1 bullet per kill - each weapon effectively holds a single chambered round, hugely amplified damage compensates for the near-empty clip.</summary>
public sealed class GameModifierOneInTheChamber : GameModifierBase
{
    private const float DamageMultiplier = 10.0f;
    private Guid _hurtHookId;

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
        _hurtHookId = Core.GameEvent.HookPost<SwiftlyS2.Shared.GameEventDefinitions.EventPlayerHurt>(OnPlayerHurt);

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
        Core.GameEvent.Unhook(_hurtHookId);
    }

    private void OnTakeDamage(ref TakeDamageEntityPreContext ctx)
    {
        if (!IsAssignedTo(CSRollUtils.GetPlayerFromEntityHandle(Core, ctx.Params.Info.Attacker)?.Slot ?? -1))
        {
            return;
        }

        ctx.Params.Info.Damage *= DamageMultiplier;
    }

    private HookResult OnPlayerHurt(SwiftlyS2.Shared.GameEventDefinitions.EventPlayerHurt @event)
    {
        if (@event.AttackerPlayer is { IsValid: true } attacker && IsAssignedTo(attacker.Slot) &&
            attacker.PlayerPawn?.WeaponServices?.ActiveWeapon.Value is { } weapon)
        {
            weapon.ReserveAmmo[0] += 1;
            weapon.ReserveAmmoUpdated();
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
