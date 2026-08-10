using SwiftlyS2.Shared.EntitySystem;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Wallhack: spawns a glowing duplicate of each x-ray target's model, visible through walls to
/// x-ray-enabled viewers only, via a two-hop entity relay chain.
///
/// A single glow prop parented directly to the real player (FollowEntity) appeared to vanish
/// after about a second regardless of viewer position or occlusion - it happened even looking
/// straight at it with no wall involved, which rules out a transmission/PVS problem entirely and
/// points at something about attaching directly to a player pawn (a complex entity with its own
/// animation graph and hitboxes) specifically. The original CS2-GameModifiers CSS plugin's proven
/// glow-effect code uses a two-hop chain instead: an invisible relay prop follows the real
/// player, and the visible glow prop follows the relay (not the player directly) - copied here
/// verbatim, including its Spawnflags=256 and entity-identity-flags clear, neither of which this
/// plugin had tried before. Requires in-game verification.
///
/// Interaction with invisibility: reported live that x-ray-enabled viewers couldn't see
/// ConditionalInvisibility/FullInvisibility targets at all - the glow prop's own FollowEntity
/// attachment to the real (now transmit-blocked) pawn appears to inherit that pawn's per-client
/// transmission state. A test fork that dropped FollowEntity in favor of per-tick Teleport-based
/// position tracking was tried and removed (didn't behave as wanted). Fixed instead at the source:
/// SetupXray grants every x-ray-enabled viewer's slot into CSRollUtils' shared xray-vision registry,
/// which GameModifierInvisibleBase reads to exempt those viewers from its own transmit-block
/// entirely - the same technique already used to let spectators see through it. Wallhack now means
/// seeing the real player too, not just their glow outline.
/// </summary>
public abstract class GameModifierXrayBase : GameModifierBase
{
    private static readonly Color TerroristGlowColor = new(255, 165, 0);
    private static readonly Color CounterTerroristGlowColor = new(135, 206, 235);
    private static readonly Color GlowPropRenderColor = new(1, 255, 255, 255);

    protected readonly HashSet<int> CachedXrayEnabledSlots = [];
    private readonly Dictionary<int, uint> _relayEntityIndex = [];
    private readonly Dictionary<int, uint> _glowPropEntityIndex = [];

    private Guid _spawnHookId;
    private Guid _deathHookId;

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
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawnEvent);
        _deathHookId = Core.GameEvent.HookPost<EventPlayerDeath>(OnPlayerDeathEvent);

        SetupXray();
    }

    protected override void OnDisabled()
    {
        Core.GameEvent.Unhook(_spawnHookId);
        Core.GameEvent.Unhook(_deathHookId);

        foreach (var slot in _glowPropEntityIndex.Keys.ToList())
        {
            RemoveXrayFromSlot(slot);
        }

        foreach (var slot in CachedXrayEnabledSlots)
        {
            CSRollUtils.RevokeXrayVision(slot);
        }

        CachedXrayEnabledSlots.Clear();
    }

    /// <summary>Determines which currently-connected players are granted x-ray vision. Evaluated once per activation (a re-roll happens naturally via random-rounds' disable/re-enable cycle).</summary>
    protected virtual void SetupXray()
    {
        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (CheckEnableXray(player))
            {
                CachedXrayEnabledSlots.Add(player.Slot);
                CSRollUtils.GrantXrayVision(player.Slot);
            }
        }

        foreach (var player in Core.PlayerManager.GetAlive())
        {
            ApplyXrayToPlayer(player);
        }
    }

    protected virtual bool CheckEnableXray(IPlayer viewer) => false;

    protected void ApplyXrayToPlayer(IPlayer target)
    {
        RemoveXrayFromSlot(target.Slot);

        var targetSlot = target.Slot;
        Core.Scheduler.NextWorldUpdate(() =>
        {
            var currentTarget = Core.PlayerManager.GetPlayer(targetSlot);
            if (currentTarget is not { IsValid: true, IsAlive: true } || currentTarget.PlayerPawn is not { } pawn)
            {
                return;
            }

            var modelName = pawn.GetModel();
            if (string.IsNullOrEmpty(modelName))
            {
                return;
            }

            var relay = CreateGlowChainProp(modelName);
            var glow = CreateGlowChainProp(modelName);

            relay.RenderMode = RenderMode_t.kRenderNone;
            relay.RenderModeUpdated();
            if (pawn.AbsOrigin is { } relayPosition)
            {
                relay.Teleport(relayPosition, null, null);
            }
            relay.AcceptInput("FollowEntity", "!activator", pawn, pawn, 0);

            glow.Render = GlowPropRenderColor;
            glow.RenderUpdated();
            // The original plugin also sets RenderMode to a "kRenderGlow" mode here, but
            // SwiftlyS2's RenderMode_t only exposes a small remapped subset (0-3, not matching
            // the true native enum) with no glow-specific value - casting an arbitrary int would
            // set the wrong mode entirely. Relying on the Glow.* properties alone, which is what
            // actually produced visible glow-through-walls rendering in earlier testing here.
            glow.Glow.GlowColorOverride = currentTarget.Controller?.Team == Team.T ? TerroristGlowColor : CounterTerroristGlowColor;
            glow.Glow.GlowColorOverrideUpdated();
            glow.Glow.GlowRange = 5000;
            glow.Glow.GlowRangeUpdated();
            glow.Glow.GlowRangeMin = 20;
            glow.Glow.GlowRangeMinUpdated();
            glow.Glow.GlowTeam = -1;
            glow.Glow.GlowTeamUpdated();
            glow.Glow.GlowType = 3;
            glow.GlowUpdated();
            if (pawn.AbsOrigin is { } glowPosition)
            {
                glow.Teleport(glowPosition, null, null);
            }
            // The glow prop follows the RELAY, not the real player directly - this is the change
            // from the previous single-hop attempt.
            glow.AcceptInput("FollowEntity", "!activator", relay, relay, 0);

            _relayEntityIndex[targetSlot] = relay.Index;
            _glowPropEntityIndex[targetSlot] = glow.Index;

            ApplyTransmitStateForAllViewers((int)relay.Index);
            ApplyTransmitStateForAllViewers((int)glow.Index);
        });
    }

    /// <summary>Creates one link of the relay chain: spawn, model, and the collision/identity setup both links share.</summary>
    private CDynamicProp CreateGlowChainProp(string modelName)
    {
        var prop = Core.EntitySystem.CreateEntityByDesignerName<CDynamicProp>("prop_dynamic");

        // Spawnflags configures the spawn process itself, so - unlike SetModel and the rest of
        // this setup - it must be set BEFORE DispatchSpawn. 256 is copied verbatim from the
        // original CS2-GameModifiers CSS plugin's proven glow-effect code; exact meaning
        // undocumented for this engine version.
        prop.Spawnflags = 256u;
        prop.SpawnflagsUpdated();

        // SetModel (and the rest of this configuration) must happen AFTER DispatchSpawn, or it
        // hits a Source 2 engine assertion ("SetupModel(): entity is still in the staging list")
        // and the model never actually gets set.
        using var keyValues = new CEntityKeyValues();
        prop.DispatchSpawn(keyValues);

        prop.SetModel(modelName);

        // Also copied verbatim from the original plugin's proven code - clears bit 2 of the
        // entity's own identity flags. Exact semantics undocumented; kept as an unexplained but
        // reproduced detail of a working reference rather than guessed at.
        if (prop.Identity is { } identity)
        {
            identity.Flags &= ~(uint)(1 << 2);
        }

        // Non-solid: these overlap the real player and must never physically collide with anyone.
        prop.Collision.CollisionGroup = (byte)CollisionGroup.Nonphysical;
        prop.Collision.CollisionGroupUpdated();
        prop.Collision.SolidFlags = 4; // FSOLID_NOT_SOLID
        prop.Collision.SolidFlagsUpdated();
        prop.Collision.SolidType = SolidType_t.SOLID_NONE;
        prop.Collision.SolidTypeUpdated();

        return prop;
    }

    /// <summary>
    /// Explicitly sets transmit state in BOTH directions for every connected viewer: blocked for
    /// non-x-ray viewers, and explicitly un-blocked (not just left as a default) for x-ray-enabled
    /// viewers. The explicit un-block for viewers is the piece a previous attempt at this same
    /// technique was missing.
    /// </summary>
    private void ApplyTransmitStateForAllViewers(int entityIndex)
    {
        foreach (var viewer in Core.PlayerManager.GetAllValidPlayers())
        {
            viewer.ShouldBlockTransmitEntity(entityIndex, !CachedXrayEnabledSlots.Contains(viewer.Slot));
        }
    }

    protected void RemoveXrayFromSlot(int slot)
    {
        if (_relayEntityIndex.Remove(slot, out var relayIndex) && Core.EntitySystem.GetEntityByIndex(relayIndex) is { IsValid: true } relayEntity)
        {
            relayEntity.Despawn();
        }

        if (_glowPropEntityIndex.Remove(slot, out var glowIndex) && Core.EntitySystem.GetEntityByIndex(glowIndex) is { IsValid: true } glowEntity)
        {
            glowEntity.Despawn();
        }
    }

    private HookResult OnPlayerSpawnEvent(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player)
        {
            ApplyXrayToPlayer(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDeathEvent(EventPlayerDeath @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player)
        {
            RemoveXrayFromSlot(player.Slot);
        }

        return HookResult.Continue;
    }

    private void OnClientConnected(IOnClientConnectedEvent @event)
    {
        if (!IsActive)
        {
            return;
        }

        var viewer = Core.PlayerManager.GetPlayer(@event.PlayerId);
        if (viewer is not { IsValid: true })
        {
            return;
        }

        // A newly connected viewer must be told about every currently glowing target, in both
        // directions - explicitly blocked if they're not x-ray-enabled, explicitly un-blocked if
        // they are (this modifier doesn't currently grant x-ray to players who connect after
        // activation, matching SetupXray's "evaluated once at activation" scope - so this branch
        // only ever applies the "block" direction in practice today, but both are handled for
        // correctness in case that scope changes later).
        var isXrayEnabled = CachedXrayEnabledSlots.Contains(viewer.Slot);
        foreach (var entityIndex in _relayEntityIndex.Values.Concat(_glowPropEntityIndex.Values))
        {
            viewer.ShouldBlockTransmitEntity((int)entityIndex, !isXrayEnabled);
        }
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        CachedXrayEnabledSlots.Remove(@event.PlayerId);
        CSRollUtils.RevokeXrayVision(@event.PlayerId);
        _relayEntityIndex.Remove(@event.PlayerId);
        _glowPropEntityIndex.Remove(@event.PlayerId);
    }
}

public sealed class GameModifierXrayAll : GameModifierXrayBase
{
    public GameModifierXrayAll()
    {
        Name = "Wallhack";
        Description = "You can see everyone through walls";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    // IsAssignedTo, not an unconditional true - when this rolls for a specific player via
    // per-player random rounds (see SupportsPerPlayerRandomization above, replacing the removed
    // RandomXray modifier's "some people get it" role), only that assigned player gets x-ray;
    // a global !addmodifier toggle (no specific assignment) still means everyone, same as before.
    protected override bool CheckEnableXray(IPlayer viewer) => IsAssignedTo(viewer.Slot);
}
