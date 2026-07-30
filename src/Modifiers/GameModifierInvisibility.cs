using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;

namespace CSRoll.Modifiers;

/// <summary>
/// Hides players from other clients' network transmit list (not a render-alpha trick - the
/// entity is genuinely never sent to non-viewers). Replaces CSS's global Listeners&lt;CheckTransmit&gt;
/// hook + manual TransmitEntities.Remove() with the per-viewer IPlayer.ShouldBlockTransmitEntity API.
/// </summary>
public abstract class GameModifierInvisibleBase : GameModifierBase
{
    protected readonly HashSet<int> CachedHiddenSlots = [];

    private Guid _deathHookId;
    private Guid _spawnHookId;
    private Guid _spawnedHookId;

    protected override void OnRegistered()
    {
        Core.Event.OnClientConnected += OnClientConnected;
        Core.Event.OnClientDisconnected += OnClientDisconnected;
    }

    protected override void OnUnregistered()
    {
        Core.Event.OnClientConnected -= OnClientConnected;
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
    }

    protected override void OnEnabled()
    {
        _deathHookId = Core.GameEvent.HookPre<EventPlayerDeath>(OnPlayerDeath);
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawnEvent);
        _spawnedHookId = Core.GameEvent.HookPost<EventPlayerSpawned>(OnPlayerSpawnedEvent);

        HidePlayers();
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_deathHookId);
        Core.GameEvent.Unhook(_spawnHookId);
        Core.GameEvent.Unhook(_spawnedHookId);

        foreach (var slot in CachedHiddenSlots.ToList())
        {
            if (Core.PlayerManager.GetPlayer(slot) is { IsValid: true } player)
            {
                UnhidePlayer(player);
            }
        }

        CachedHiddenSlots.Clear();
    }

    protected virtual void HidePlayers()
    {
        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (CheckHidePlayer(player))
            {
                HidePlayer(player);
            }
        }
    }

    protected virtual bool CheckHidePlayer(IPlayer player) => false;

    protected void HidePlayer(IPlayer target)
    {
        if (target.PlayerPawn is not { } pawn || !CachedHiddenSlots.Add(target.Slot))
        {
            return;
        }

        var entityId = (int)pawn.Index;
        foreach (var viewer in Core.PlayerManager.GetAllValidPlayers())
        {
            if (viewer.Slot != target.Slot)
            {
                viewer.ShouldBlockTransmitEntity(entityId, true);
            }
        }
    }

    protected void UnhidePlayer(IPlayer target)
    {
        if (!CachedHiddenSlots.Remove(target.Slot) || target.PlayerPawn is not { } pawn)
        {
            return;
        }

        var entityId = (int)pawn.Index;
        foreach (var viewer in Core.PlayerManager.GetAllValidPlayers())
        {
            viewer.ShouldBlockTransmitEntity(entityId, false);
        }
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player)
        {
            UnhidePlayer(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawnEvent(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && CheckHidePlayer(player))
        {
            HidePlayer(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawnedEvent(EventPlayerSpawned @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && CheckHidePlayer(player))
        {
            HidePlayer(player);
        }

        return HookResult.Continue;
    }

    private void OnClientConnected(IOnClientConnectedEvent @event)
    {
        // ShouldBlockTransmitEntity is per-viewer, so a newly connected viewer must be told
        // about every currently-hidden target individually.
        if (CachedHiddenSlots.Count == 0)
        {
            return;
        }

        var viewer = Core.PlayerManager.GetPlayer(@event.PlayerId);
        if (viewer is not { IsValid: true })
        {
            return;
        }

        foreach (var hiddenSlot in CachedHiddenSlots)
        {
            if (Core.PlayerManager.GetPlayer(hiddenSlot)?.PlayerPawn is { } pawn)
            {
                viewer.ShouldBlockTransmitEntity((int)pawn.Index, true);
            }
        }
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        // The disconnecting player's entity is going away regardless - just drop our
        // bookkeeping, no network calls needed against them or on their behalf.
        CachedHiddenSlots.Remove(@event.PlayerId);
    }
}

