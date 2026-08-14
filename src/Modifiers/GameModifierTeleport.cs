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
            "TeleportOnReload",
            "TeleportOnHit",
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

        // Bug fix: self-kill check used to compare SteamID - bot SteamID is fixed at 0, so a bot
        // killing a different bot was misread as a self-kill and silently excluded.
        if (attacker is not { IsValid: true } || victim is not { IsValid: true } || CSRollUtils.IsSamePlayer(attacker, victim) || !IsAssignedTo(attacker.Slot))
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
            "TeleportOnReload",
            "TeleportOnHit",
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
