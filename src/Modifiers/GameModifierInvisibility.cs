using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;

using CSRoll.Core;

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
///
/// Wallhack exemption: alive x-ray-enabled viewers (CSRollUtils.HasXrayVision) are exempted from the
/// block the same way spectators are - reported live that Wallhack couldn't see invisible targets at
/// all, and "wallhack lets you see through walls" reasonably ought to include seeing through
/// invisibility too. GameModifierXrayBase grants/revokes into that shared registry as Wallhack
/// activates/deactivates for a player; XrayVisionGranted/Revoked below react immediately so this
/// doesn't wait for the next spawn/death to take effect.
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
        CSRollUtils.XrayVisionGranted += OnXrayVisionGranted;
        CSRollUtils.XrayVisionRevoked += OnXrayVisionRevoked;
    }

    protected override void OnUnregistered()
    {
        CSRollUtils.XrayVisionGranted -= OnXrayVisionGranted;
        CSRollUtils.XrayVisionRevoked -= OnXrayVisionRevoked;
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
            // spectator mode, and alive x-ray-enabled viewers, are deliberately left able to see
            // hidden targets (see class doc comment).
            if (viewer.Slot != target.Slot && viewer.IsAlive && !CSRollUtils.HasXrayVision(viewer.Slot))
            {
                viewer.ShouldBlockTransmitEntity(entityId, true);
            }
        }
    }

    /// <summary>
    /// Resyncs one viewer's transmit-block state against every currently-hidden OTHER target - true
    /// (block) for a viewer who just became alive/active, false (unblock) for one who just became a
    /// spectator or just gained x-ray vision. An x-ray-enabled viewer is never actually blocked
    /// regardless of what the caller asked for - it's the single place that rule is enforced for
    /// every resync path (spawn, death, grant/revoke).
    /// </summary>
    private void ResyncHiddenTargetsForViewer(IPlayer viewer, bool block)
    {
        var effectiveBlock = block && !CSRollUtils.HasXrayVision(viewer.Slot);

        foreach (var hiddenSlot in CachedHiddenSlots)
        {
            if (hiddenSlot == viewer.Slot)
            {
                continue;
            }

            if (Core.PlayerManager.GetPlayer(hiddenSlot)?.PlayerPawn is { } pawn)
            {
                viewer.ShouldBlockTransmitEntity((int)pawn.Index, effectiveBlock);
            }
        }
    }

    private void OnXrayVisionGranted(int slot)
    {
        if (Core.PlayerManager.GetPlayer(slot) is { IsValid: true } viewer)
        {
            ResyncHiddenTargetsForViewer(viewer, block: false);
        }
    }

    private void OnXrayVisionRevoked(int slot)
    {
        // Losing x-ray while dead/spectating shouldn't suddenly block them - only re-block if
        // they're actually back to being a live combatant, matching the "only alive players are
        // denied sight" rule everywhere else in this class.
        if (Core.PlayerManager.GetPlayer(slot) is { IsValid: true, IsAlive: true } viewer)
        {
            ResyncHiddenTargetsForViewer(viewer, block: true);
        }
    }

    protected void UnhidePlayer(IPlayer target)
    {
        // Bug fix: this used to short-circuit on `!CachedHiddenSlots.Remove(slot) || pawn is null`,
        // and || evaluates left to right - so when the pawn couldn't be resolved (the normal state
        // for a moment after death, which is exactly when Vanish unhides on the death path) the slot
        // was dropped from the cache and the function returned WITHOUT ever issuing
        // ShouldBlockTransmitEntity(id, false). The per-viewer block on that entity index was then
        // never lifted, and CS2 recycles entity indices. Checking the pawn first and leaving the slot
        // cached when it can't be resolved means a later call can still complete the unhide.
        if (target.PlayerPawn is not { } pawn)
        {
            return;
        }

        if (!CachedHiddenSlots.Remove(target.Slot))
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
        // they're somehow already alive (e.g. a hot-reload mid-round), matching the "only alive,
        // non-x-ray players are denied sight" rule everywhere else in this class.
        var block = viewer.IsAlive && !CSRollUtils.HasXrayVision(viewer.Slot);
        foreach (var hiddenSlot in CachedHiddenSlots)
        {
            if (Core.PlayerManager.GetPlayer(hiddenSlot)?.PlayerPawn is { } pawn)
            {
                viewer.ShouldBlockTransmitEntity((int)pawn.Index, block);
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

