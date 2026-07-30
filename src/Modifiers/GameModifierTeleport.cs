using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>On a kill, the killer is teleported to their victim's death position.</summary>
public sealed class GameModifierSwapPlacesOnKill : GameModifierBase
{
    private Guid _deathHookId;

    public GameModifierSwapPlacesOnKill()
    {
        Name = "SwapOnDeath";
        Description = "Players will swap places on kill";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = [
            "SwapOnHit",
            "ResetOnReload",
        ];
    }

    protected override void OnEnabled()
    {
        _deathHookId = Core.GameEvent.HookPost<EventPlayerDeath>(OnPlayerDeath);
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_deathHookId);
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event)
    {
        var attacker = @event.AttackerPlayer;
        var victim = @event.UserIdPlayer;

        if (attacker is not { IsValid: true } || victim is not { IsValid: true } || attacker.SteamID == victim.SteamID || !IsAssignedTo(attacker.Slot))
        {
            return HookResult.Continue;
        }

        if (victim.PlayerPawn?.AbsOrigin is { } deathPosition)
        {
            CSRollUtils.TeleportPlayer(Core, attacker, deathPosition);
        }

        return HookResult.Continue;
    }
}

/// <summary>Attacker and victim swap positions on every hit.</summary>
public sealed class GameModifierSwapPlacesOnHit : GameModifierBase
{
    private Guid _hurtHookId;

    public GameModifierSwapPlacesOnHit()
    {
        Name = "SwapOnHit";
        Description = "Players will swap places on hit";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = [
            "SwapOnDeath",
            "ResetOnReload",
        ];
    }

    protected override void OnEnabled()
    {
        _hurtHookId = Core.GameEvent.HookPost<EventPlayerHurt>(OnPlayerHurt);
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_hurtHookId);
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event)
    {
        if (@event.AttackerPlayer is { IsValid: true } attacker && IsAssignedTo(attacker.Slot) && @event.UserIdPlayer is { IsValid: true } victim)
        {
            CSRollUtils.SwapPlayerLocations(Core, attacker, victim);
        }

        return HookResult.Continue;
    }
}

/// <summary>Reloading teleports the player back to their own team's spawn area.</summary>
public sealed class GameModifierResetOnReload : GameModifierBase
{
    private Guid _reloadHookId;

    public GameModifierResetOnReload()
    {
        Name = "ResetOnReload";
        Description = "Players are teleported back to their spawn on reload";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = [
            "SwapOnDeath",
            "SwapOnHit",
        ];
    }

    protected override void OnEnabled()
    {
        _reloadHookId = Core.GameEvent.HookPost<EventWeaponReload>(OnPlayerReload);
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_reloadHookId);
    }

    private HookResult OnPlayerReload(EventWeaponReload @event)
    {
        var player = @event.UserIdPlayer;
        if (player is not { IsValid: true, IsAlive: true } || !IsAssignedTo(player.Slot) || player.Controller is not { } controller)
        {
            return HookResult.Continue;
        }

        if (CSRollUtils.GetSpawnLocation(Core, controller.Team) is { } spawnPosition)
        {
            CSRollUtils.TeleportPlayer(Core, player, spawnPosition);
        }

        return HookResult.Continue;
    }
}
