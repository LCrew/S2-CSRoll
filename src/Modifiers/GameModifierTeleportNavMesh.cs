using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Bug fix: these two used to teleport to a random NavMesh position (or, failing that, a random T
/// OR CT spawn, whichever team) - reported live as breaking Wingman maps, where a teleport into the
/// wrong team's small spawn room is a serious, round-ending problem, not just a curiosity. Always
/// teleporting to a random spawn point belonging to the player's OWN team (CSRollUtils.GetSpawnLocation
/// with their actual Team) removes both the NavMesh dependency and the wrong-team-spawn risk entirely.
/// </summary>
public sealed class GameModifierTeleportOnReload : GameModifierBase
{
    private Guid _reloadHookId;

    public GameModifierTeleportOnReload()
    {
        Name = "TeleportOnReload";
        Description = "Players are teleported to their spawn on reload";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = [
            "TeleportOnHit",
            "ResetOnReload",
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
        if (@event.UserIdPlayer is not { IsValid: true, IsAlive: true } player || !IsAssignedTo(player.Slot) ||
            player.Controller is not { IsValid: true } controller)
        {
            return HookResult.Continue;
        }

        if (CSRollUtils.GetSpawnLocation(Core, controller.Team) is { } position)
        {
            CSRollUtils.TeleportPlayer(Core, player, position);
        }

        return HookResult.Continue;
    }
}

public sealed class GameModifierTeleportOnHit : GameModifierBase
{
    private Guid _hurtHookId;

    public GameModifierTeleportOnHit()
    {
        Name = "TeleportOnHit";
        Description = "Players are teleported to their spawn on hit";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
        IncompatibleModifiers = [
            "TeleportOnReload",
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
        if (@event.UserIdPlayer is not { IsValid: true, IsAlive: true } player || !IsAssignedTo(player.Slot) ||
            player.Controller is not { IsValid: true } controller)
        {
            return HookResult.Continue;
        }

        if (CSRollUtils.GetSpawnLocation(Core, controller.Team) is { } position)
        {
            CSRollUtils.TeleportPlayer(Core, player, position);
        }

        return HookResult.Continue;
    }
}
