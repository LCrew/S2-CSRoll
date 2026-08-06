using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Player-triggered ability, separate from TeleportOnReload/TeleportOnHit: starting on a cooldown
/// (CooldownSeconds, reset on activation and again after every spawn and every use), pressing the
/// "Inspect Weapon" button teleports the assigned player directly behind a random living enemy,
/// facing the same direction that enemy is.
///
/// The "Inspect Weapon" (+lookatweapon) bind has no named entry in SwiftlyS2.Shared.Events.
/// GameButtonFlags - SwiftlyS2 labels its members by their default keybind rather than the engine
/// action, and inspect defaults to F. Confirmed (not guessed) by cross-referencing the raw bit value:
/// GameButtonFlags.F = 34359738368 (2^35), which matches CounterStrikeSharp's own
/// InputBitMask_t.IN_LOOK_AT_WEAPON = 34359738368 exactly - the same underlying engine bit, just
/// named differently by each SDK's authors.
///
/// No explicit edge-detection is needed to stop a held key from firing repeatedly: the moment it
/// fires, NextAvailableTime is pushed CooldownSeconds into the future, so the very next tick (even
/// with the button still held) fails the readiness check. It can only fire again once the cooldown
/// has genuinely elapsed AND the button is down at that instant (or any tick after).
/// </summary>
public sealed class GameModifierFlankTeleport : GameModifierBase
{
    private readonly Dictionary<int, float> _nextAvailableTime = [];

    private Guid _spawnHookId;

    public GameModifierFlankTeleport()
    {
        Name = "FlankTeleport";
        Description = "After a cooldown, press Inspect Weapon to teleport behind a random enemy";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnRegistered()
    {
        Core.Event.OnClientDisconnected += OnClientDisconnected;
    }

    protected override void OnUnregistered()
    {
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
    }

    protected override void OnEnabled()
    {
        Core.Event.OnTick += OnTick;
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);

        var readyAt = Core.Engine.GlobalVars.CurrentTime + Runtime.Config.FlankTeleport.CooldownSeconds;
        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (IsAssignedTo(player.Slot))
            {
                _nextAvailableTime[player.Slot] = readyAt;
            }
        }
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= OnTick;
        Core.GameEvent.Unhook(_spawnHookId);
        _nextAvailableTime.Clear();
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            _nextAvailableTime[player.Slot] = Core.Engine.GlobalVars.CurrentTime + Runtime.Config.FlankTeleport.CooldownSeconds;
        }

        return HookResult.Continue;
    }

    private void OnTick()
    {
        var now = Core.Engine.GlobalVars.CurrentTime;

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (!IsAssignedTo(player.Slot) || !player.IsAlive)
            {
                continue;
            }

            if (!player.PressedButtons.HasFlag(GameButtonFlags.F))
            {
                continue;
            }

            if (now < _nextAvailableTime.GetValueOrDefault(player.Slot, now))
            {
                continue;
            }

            if (player.Controller is not { IsValid: true } controller)
            {
                continue;
            }

            var enemyTeam = controller.Team == Team.T ? Team.CT : Team.T;
            var enemies = Core.PlayerManager.GetInTeam(enemyTeam)
                .Where(p => p.IsValid && p.IsAlive && p.PlayerPawn is not null)
                .ToList();

            // Deliberately not consuming the cooldown here - no valid target is bad luck, not a
            // wasted use, so the player can just try again the moment one becomes available.
            if (enemies.Count == 0)
            {
                continue;
            }

            var target = enemies[Random.Shared.Next(enemies.Count)];
            var targetPawn = target.PlayerPawn!;

            if (targetPawn.AbsOrigin is not { } targetOrigin)
            {
                continue;
            }

            targetPawn.EyeAngles.ToDirectionVectors(out var forward, out _, out _);
            var behindPosition = targetOrigin - (forward * Runtime.Config.FlankTeleport.TeleportDistance);

            CSRollUtils.TeleportPlayer(Core, player, behindPosition, targetPawn.EyeAngles);
            _nextAvailableTime[player.Slot] = now + Runtime.Config.FlankTeleport.CooldownSeconds;

            var targetName = target.Controller is { IsValid: true } targetController ? targetController.PlayerName : "an enemy";
            CSRollUtils.PrintTitleToChat(Core, player, $"Teleported behind {targetName}!");
        }
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _nextAvailableTime.Remove(@event.PlayerId);
    }
}
