using System.Collections.Concurrent;

using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;

namespace CSRoll.Modifiers;

/// <summary>
/// Scales every player's model. SwiftlyS2's CBaseModelEntity.SetScale(float) handles the
/// entity-input/dirty-flag plumbing internally - no manual AcceptInput+deferred-frame hack needed here.
/// </summary>
public abstract class GameModifierScalePlayer : GameModifierBase
{
    private readonly ConcurrentDictionary<int, float> _cachedOriginalScale = new();
    private Guid _spawnHookId;

    protected abstract float GetScale();

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
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (IsAssignedTo(player.Slot))
            {
                ApplyScale(player);
            }
        }
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_spawnHookId);

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (IsAssignedTo(player.Slot))
            {
                ResetScale(player);
            }
        }
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            ApplyScale(player);
        }

        return HookResult.Continue;
    }

    private void ApplyScale(IPlayer player)
    {
        if (player.PlayerPawn is not { } pawn)
        {
            return;
        }

        _cachedOriginalScale[player.Slot] = pawn.CBodyComponent?.SceneNode?.Scale ?? 1.0f;
        pawn.SetScale(GetScale());
    }

    private void ResetScale(IPlayer player)
    {
        if (player.PlayerPawn is not { } pawn)
        {
            return;
        }

        var originalScale = _cachedOriginalScale.TryGetValue(player.Slot, out var cached) ? cached : 1.0f;
        pawn.SetScale(originalScale);
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        // Bug fix vs. the CSS original: unconditional TryRemove, no inverted ContainsKey guard.
        _cachedOriginalScale.TryRemove(@event.PlayerId, out _);
    }
}

public sealed class GameModifierSmallPlayers : GameModifierScalePlayer
{
    public GameModifierSmallPlayers()
    {
        Name = "SmallPlayers";
        Description = "Everyone is 2X smaller";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override float GetScale() => 0.5f;
}
