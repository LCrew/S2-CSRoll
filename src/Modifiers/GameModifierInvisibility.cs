using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;

namespace CSRoll.Modifiers;

/// <summary>
/// Hides players from other clients' network transmit list (not a render-alpha trick - the
/// entity is genuinely never sent to non-viewers). Replaces CSS's global Listeners&lt;CheckTransmit&gt;
/// hook + manual TransmitEntities.Remove() with the per-viewer IPlayer.ShouldBlockTransmitEntity API.
///
/// Bug fix: the transmit block used to apply to every OTHER connected slot unconditionally,
/// including dead players/admins in spectator mode - a spectator whose observer camera followed a
/// hidden target simply had nothing to render at all (not a cosmetic "can't see them" but a fully
/// blank feed), and this was true even for the hidden player's own teammates spectating them after
/// dying. Spectating isn't a competitive-advantage concern the way live gameplay sight is, so the
/// block now only ever applies to currently-ALIVE other players; a player who dies is immediately
/// unblocked from every still-hidden target (OnAnyPlayerDeath), and re-blocked the moment they
/// respawn into a new life (OnPlayerSpawnEvent/OnPlayerSpawnedEvent already ran per-spawn - extended
/// to also resync every OTHER already-hidden target's block state against the newly-alive viewer).
/// </summary>
public abstract class GameModifierInvisibleBase : GameModifierBase
{
    protected readonly HashSet<int> CachedHiddenSlots = [];

    private Guid _deathHookId;
    private Guid _deathSpectateHookId;
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
        _deathSpectateHookId = Core.GameEvent.HookPost<EventPlayerDeath>(OnAnyPlayerDeath);
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawnEvent);
        _spawnedHookId = Core.GameEvent.HookPost<EventPlayerSpawned>(OnPlayerSpawnedEvent);

        HidePlayers();
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_deathHookId);
        Core.GameEvent.Unhook(_deathSpectateHookId);
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
            // Only currently-alive other players are denied sight - dead players/admins in
            // spectator mode are deliberately left able to see hidden targets (see class doc
            // comment for why blocking them broke spectating entirely).
            if (viewer.Slot != target.Slot && viewer.IsAlive)
            {
                viewer.ShouldBlockTransmitEntity(entityId, true);
            }
        }
    }

    /// <summary>Resyncs one viewer's transmit-block state against every currently-hidden OTHER target - true (block) for a viewer who just became alive/active, false (unblock) for one who just became a spectator.</summary>
    private void ResyncHiddenTargetsForViewer(IPlayer viewer, bool block)
    {
        foreach (var hiddenSlot in CachedHiddenSlots)
        {
            if (hiddenSlot == viewer.Slot)
            {
                continue;
            }

            if (Core.PlayerManager.GetPlayer(hiddenSlot)?.PlayerPawn is { } pawn)
            {
                viewer.ShouldBlockTransmitEntity((int)pawn.Index, block);
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

    /// <summary>A player who just died becomes a spectator - unblock every still-hidden OTHER target for them so they can actually spectate (see class doc comment).</summary>
    private HookResult OnAnyPlayerDeath(EventPlayerDeath @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player)
        {
            ResyncHiddenTargetsForViewer(player, block: false);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawnEvent(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player)
        {
            if (CheckHidePlayer(player))
            {
                HidePlayer(player);
            }

            // Back in the game as an active combatant - re-block every OTHER still-hidden target.
            ResyncHiddenTargetsForViewer(player, block: true);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawnedEvent(EventPlayerSpawned @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player)
        {
            if (CheckHidePlayer(player))
            {
                HidePlayer(player);
            }

            ResyncHiddenTargetsForViewer(player, block: true);
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

        // A freshly connected client isn't alive yet (still on no team/spectator) - only block if
        // they're somehow already alive (e.g. a hot-reload mid-round), matching the "only alive
        // players are denied sight" rule everywhere else in this class.
        foreach (var hiddenSlot in CachedHiddenSlots)
        {
            if (Core.PlayerManager.GetPlayer(hiddenSlot)?.PlayerPawn is { } pawn)
            {
                viewer.ShouldBlockTransmitEntity((int)pawn.Index, viewer.IsAlive);
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

