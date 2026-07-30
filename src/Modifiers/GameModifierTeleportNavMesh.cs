using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;

using CSRoll.Core;
using CSRoll.Services.Interfaces;

namespace CSRoll.Modifiers;

/// <summary>
/// These two modifiers prefer INavMeshService's true random-nav-area positions, but no longer
/// depend on the signature scan succeeding: if IsAvailable is false (scan failed, or
/// EnableNavMeshTeleports is off), GetRandomPosition() returns null and each hook falls back to
/// CSRollUtils.GetRandomSpawnLocation() - a plain random T/CT spawn point, always
/// available. That keeps these registered and testable even on a CS2 build whose binary
/// doesn't match the NavMesh signatures, at the cost of less varied teleport destinations.
/// </summary>
public sealed class GameModifierTeleportOnReload : GameModifierBase
{
    private readonly INavMeshService _navMesh;
    private Guid _reloadHookId;

    public GameModifierTeleportOnReload(INavMeshService navMesh)
    {
        _navMesh = navMesh;
        Name = "TeleportOnReload";
        Description = "Players are teleported to a random spot on reload";
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
        if (@event.UserIdPlayer is not { IsValid: true, IsAlive: true } player || !IsAssignedTo(player.Slot))
        {
            return HookResult.Continue;
        }

        var position = _navMesh.GetRandomPosition() ?? CSRollUtils.GetRandomSpawnLocation(Core);
        if (position is { } pos)
        {
            CSRollUtils.TeleportPlayer(Core, player, pos);
        }

        return HookResult.Continue;
    }
}

public sealed class GameModifierTeleportOnHit : GameModifierBase
{
    private readonly INavMeshService _navMesh;
    private Guid _hurtHookId;

    public GameModifierTeleportOnHit(INavMeshService navMesh)
    {
        _navMesh = navMesh;
        Name = "TeleportOnHit";
        Description = "Players are teleported to a random spot on hit";
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
        if (@event.UserIdPlayer is not { IsValid: true, IsAlive: true } player || !IsAssignedTo(player.Slot))
        {
            return HookResult.Continue;
        }

        var position = _navMesh.GetRandomPosition() ?? CSRollUtils.GetRandomSpawnLocation(Core);
        if (position is { } pos)
        {
            CSRollUtils.TeleportPlayer(Core, player, pos);
        }

        return HookResult.Continue;
    }
}
