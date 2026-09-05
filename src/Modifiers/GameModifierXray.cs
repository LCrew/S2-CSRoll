using Microsoft.Extensions.Logging;

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
/// ConditionalInvisibility/Vanish targets at all - the glow prop's own FollowEntity
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

    /// <summary>The only classname this modifier ever creates - see DespawnTrackedProp for why it has to be checked before anything is destroyed.</summary>
    private const string GlowChainPropDesignerName = "prop_dynamic";

    /// <summary>
    /// How long the glow-chain build is deferred after ApplyXrayToPlayer is called. Small but
    /// deliberately non-zero: the deferral exists because the spawn path calls in before the pawn's
    /// model is reliably assigned, so it has to land on a later frame, not merely later in this one.
    /// </summary>
    private const float GlowChainBuildDelaySeconds = 0.1f;

    protected readonly HashSet<int> CachedXrayEnabledSlots = [];
    private readonly Dictionary<int, uint> _relayEntityIndex = [];
    private readonly Dictionary<int, uint> _glowPropEntityIndex = [];

    /// <summary>
    /// Bumped on every activation and deactivation. ApplyXrayToPlayer defers all of its entity
    /// creation into a NextWorldUpdate callback, and that callback used to run unconditionally.
    ///
    /// Bug fix: if the modifier deactivated between the queue and the callback - round end,
    /// RemoveAllModifiers, the double EventRoundStart CS2 fires out of warmup, a disconnect -
    /// OnDisabled walked an empty dictionary, despawned nothing, and then the callback fired anyway
    /// and built two props that nothing owned, nothing tracked and nothing would ever remove. It
    /// also wrote their indices back into the dictionaries of a now-inactive modifier, which is how
    /// those indices went stale in the first place and set up the recycled-index despawn below.
    /// The callback now refuses to build anything once its generation has been superseded.
    /// </summary>
    private int _activationGeneration;

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
        _activationGeneration++;

        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawnEvent);
        _deathHookId = Core.GameEvent.HookPost<EventPlayerDeath>(OnPlayerDeathEvent);
        Core.Event.OnTick += RefreshRadarSpotting;

        SetupXray();
    }

    protected override void OnDisabled()
    {
        _activationGeneration++;

        Core.GameEvent.Unhook(_spawnHookId);
        Core.GameEvent.Unhook(_deathHookId);
        Core.Event.OnTick -= RefreshRadarSpotting;

        // Before CachedXrayEnabledSlots is cleared below - it is the set of bits to take back.
        ClearRadarSpotting();

        // Union of both dictionaries rather than just the glow one: they are written together today,
        // but teardown that only consults one of them is one edit away from leaking the other.
        foreach (var slot in _glowPropEntityIndex.Keys.Concat(_relayEntityIndex.Keys).Distinct().ToList())
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

        // The x-ray-vision grant above is the half of this modifier that costs nothing and cannot
        // crash - it only writes into CSRollUtils' shared registry, which GameModifierInvisibleBase
        // reads to let a Wallhack holder see through ConditionalInvisibility/Vanish. The glow-prop
        // chain below is the half that has been confirmed live to kill the server, so it is behind
        // its own switch and off by default. See XrayConfig.GlowProps.
        if (!Runtime.Config.Xray.GlowProps)
        {
            Core.Logger.LogInformation(
                "[CSRoll] XRAY: glow-prop chain disabled (Xray.GlowProps=false). Wallhack is running on radar spotting (Xray.RadarSpotting={Radar}) for {Count} viewer(s).",
                Runtime.Config.Xray.RadarSpotting, CachedXrayEnabledSlots.Count);
            return;
        }

        foreach (var player in Core.PlayerManager.GetAlive())
        {
            ApplyXrayToPlayer(player);
        }
    }

    protected virtual bool CheckEnableXray(IPlayer viewer) => false;

    /// <summary>
    /// The wallhack itself: marks every other alive player as "spotted" for the x-ray holders only,
    /// so they hold everyone on the radar through walls.
    ///
    /// CCSPlayerPawn.EntitySpottedState.SpottedByMask is a per-viewer bitmask the engine already
    /// maintains - one bit per player slot, two 32-bit words. Setting a bit is an ordinary networked
    /// schema write on a pawn that already exists, which is the same thing every other modifier in
    /// this plugin does safely. Crucially it creates no entities, dispatches no entity I/O and
    /// despawns nothing, so it avoids all three calls that were confirmed live to crash the server
    /// through the GlowProps chain.
    ///
    /// Re-asserted every tick rather than set once: the engine recomputes real visibility each frame
    /// and clears bits it does not agree with, so a one-shot write lasts a single frame. The
    /// already-set check keeps this to a network update only on the frames the engine actually
    /// cleared the bit.
    /// </summary>
    private void RefreshRadarSpotting()
    {
        if (!Runtime.Config.Xray.RadarSpotting || CachedXrayEnabledSlots.Count == 0)
        {
            return;
        }

        foreach (var target in Core.PlayerManager.GetAlive())
        {
            if (target.PlayerPawn is not { } pawn)
            {
                continue;
            }

            var changed = false;
            foreach (var viewerSlot in CachedXrayEnabledSlots)
            {
                if (viewerSlot != target.Slot)
                {
                    changed |= SetSpottedBit(pawn, viewerSlot, spotted: true);
                }
            }

            if (changed)
            {
                pawn.EntitySpottedState.SpottedByMaskUpdated();
            }
        }
    }

    /// <summary>Takes back every bit this modifier set, so a holder losing x-ray stops seeing the radar contacts immediately rather than waiting for the engine to notice.</summary>
    private void ClearRadarSpotting()
    {
        foreach (var target in Core.PlayerManager.GetAllValidPlayers())
        {
            if (target.PlayerPawn is not { } pawn)
            {
                continue;
            }

            var changed = false;
            foreach (var viewerSlot in CachedXrayEnabledSlots)
            {
                changed |= SetSpottedBit(pawn, viewerSlot, spotted: false);
            }

            if (changed)
            {
                pawn.EntitySpottedState.SpottedByMaskUpdated();
            }
        }
    }

    /// <summary>
    /// Flips one viewer's bit in a pawn's spotted mask, returning whether it actually changed so the
    /// caller can skip the network update when nothing moved.
    ///
    /// Bounds-checked against the array's own ElementCount rather than an assumed length of two. A
    /// slot index past the end would otherwise be a raw out-of-bounds write straight into engine
    /// memory - the exact failure mode that has already cost this plugin several crash hunts.
    /// </summary>
    private static bool SetSpottedBit(CCSPlayerPawn pawn, int viewerSlot, bool spotted)
    {
        var mask = pawn.EntitySpottedState.SpottedByMask;
        var word = viewerSlot / 32;
        if (viewerSlot < 0 || word >= mask.ElementCount)
        {
            return false;
        }

        var bit = 1u << (viewerSlot % 32);
        var isSet = (mask[word] & bit) != 0;
        if (isSet == spotted)
        {
            return false;
        }

        if (spotted)
        {
            mask[word] |= bit;
        }
        else
        {
            mask[word] &= ~bit;
        }

        return true;
    }

    /// <summary>
    /// Crash breadcrumb for this modifier specifically. Wallhack was confirmed live to kill the
    /// server the moment it activates, and every call below crosses into the engine - entity
    /// creation, model assignment, entity I/O parenting, raw identity-flag and collision writes.
    /// A fault in any of them takes the process down without unwinding, so the console's last XRAY
    /// line names the exact call that was executing. Kept unconditional for the same reason as the
    /// ACTIVATING/ACTIVATED pairs in GameModifierBase: this has to be on before the crash, not
    /// after someone predicts it.
    /// </summary>
    private void Step(int slot, string step) => Core.Logger.LogInformation("[CSRoll] XRAY slot {Slot}: {Step}", slot, step);

    /// <summary>
    /// Brackets one engine call with a before AND an after line.
    ///
    /// Single-sided logging cost two rounds of live testing to nothing. Both crash logs ended on a
    /// complete step line followed by a HALF-WRITTEN timestamp header - the logger had already begun
    /// flushing the next entry when the process died - so "the last line printed" was never provably
    /// "the last call executed", and each round narrowed the fault by exactly one statement.
    ///
    /// With a pair per call the reading rule stops depending on the trailing line surviving: the
    /// last "ok {step}" is the last call that definitely COMPLETED, so the fault is in whatever runs
    /// next, whether or not its own "-> {step}" line made it out.
    /// </summary>
    private void Do(int slot, string step, Action call)
    {
        Core.Logger.LogInformation("[CSRoll] XRAY slot {Slot}: -> {Step}", slot, step);
        call();
        Core.Logger.LogInformation("[CSRoll] XRAY slot {Slot}: ok {Step}", slot, step);
    }

    protected void ApplyXrayToPlayer(IPlayer target)
    {
        // Runs before the GlowProps gate below, deliberately: if the switch is flipped off at
        // runtime (config hot-reload) while props are already standing, this is what takes them
        // down rather than stranding them in the map for the rest of the round.
        RemoveXrayFromSlot(target.Slot);

        // Gated here rather than only in SetupXray because EventPlayerSpawn calls in here directly.
        if (!Runtime.Config.Xray.GlowProps)
        {
            return;
        }

        var targetSlot = target.Slot;
        var generation = _activationGeneration;

        // Bug fix candidate for the live crash: this build used to run inside
        // Core.Scheduler.NextWorldUpdate. Every other NextWorldUpdate in this codebase only writes
        // schema fields or teleports an existing entity - this was the sole place CREATING entities
        // from inside one, and it is the only entity-creating code that crashes. MasterZeus creates
        // info_particle_system entities happily, but it does so straight from OnTick, on a real game
        // frame. NextWorldUpdate lands at a different point in the frame, and the engine's entity
        // factory is not obviously safe to call there.
        //
        // Moved onto DelayBySeconds, which this codebase already documents (see
        // ModifierRuntime.PlaySpinThenReveal) as "the one delay primitive already confirmed working
        // in this codebase" after DelayAndRepeatBySeconds turned out not to fire live at all. The
        // deferral itself must stay: ApplyXrayToPlayer is called from EventPlayerSpawn, where the
        // pawn's model is not reliably assigned yet, which is why this was deferred to begin with.
        Core.Scheduler.DelayBySeconds(GlowChainBuildDelaySeconds, () =>
        {
            // See _activationGeneration: a roll that was superseded while this callback sat in the
            // queue must not build props nothing will ever own.
            if (!IsActive || _activationGeneration != generation)
            {
                Step(targetSlot, "skipped - deactivated or superseded before the deferred build ran");
                return;
            }

            var currentTarget = Core.PlayerManager.GetPlayer(targetSlot);
            if (currentTarget is not { IsValid: true, IsAlive: true } || currentTarget.PlayerPawn is not { } pawn)
            {
                return;
            }

            Step(targetSlot, "GetModel");
            var modelName = pawn.GetModel();

            // Logged as its own step rather than folded into the next message. The live crash log
            // ended on "GetModel" followed by a HALF-WRITTEN timestamp header, which means a further
            // entry had begun flushing as the process died - so the logger is racing the fault and
            // the last complete line cannot be trusted to be the last statement executed. Splitting
            // the two candidates onto separate lines is what makes the next log decisive: ending on
            // "GetModel" means GetModel() itself faulted, ending on "model resolved" means it
            // returned and the fault is in entity creation.
            Step(targetSlot, $"model resolved: {modelName}");

            if (string.IsNullOrEmpty(modelName))
            {
                return;
            }

            Step(targetSlot, "create relay prop");
            var relay = CreateGlowChainProp(modelName);
            Step(targetSlot, "create glow prop");
            var glow = CreateGlowChainProp(modelName);

            Do(targetSlot, "relay RenderMode", () =>
            {
                relay.RenderMode = RenderMode_t.kRenderNone;
                relay.RenderModeUpdated();
            });

            if (pawn.AbsOrigin is { } relayPosition)
            {
                Do(targetSlot, "relay Teleport", () => relay.Teleport(relayPosition, null, null));
            }

            Do(targetSlot, "relay FollowEntity -> pawn", () => relay.AcceptInput("FollowEntity", "!activator", pawn, pawn, 0));

            Do(targetSlot, "glow Render", () =>
            {
                glow.Render = GlowPropRenderColor;
                glow.RenderUpdated();
            });

            // The original plugin also sets RenderMode to a "kRenderGlow" mode here, but
            // SwiftlyS2's RenderMode_t only exposes a small remapped subset (0-3, not matching
            // the true native enum) with no glow-specific value - casting an arbitrary int would
            // set the wrong mode entirely. Relying on the Glow.* properties alone, which is what
            // actually produced visible glow-through-walls rendering in earlier testing here.
            //
            // Each Glow.* write is bracketed separately: CGlowProperty is a nested schema object on
            // the prop, so every one of these is an independent offset resolution and an independent
            // networked write, and any one of them could be the bad one.
            Do(targetSlot, "glow GlowColorOverride", () =>
            {
                glow.Glow.GlowColorOverride = currentTarget.Controller?.Team == Team.T ? TerroristGlowColor : CounterTerroristGlowColor;
                glow.Glow.GlowColorOverrideUpdated();
            });
            Do(targetSlot, "glow GlowRange", () =>
            {
                glow.Glow.GlowRange = 5000;
                glow.Glow.GlowRangeUpdated();
            });
            Do(targetSlot, "glow GlowRangeMin", () =>
            {
                glow.Glow.GlowRangeMin = 20;
                glow.Glow.GlowRangeMinUpdated();
            });
            Do(targetSlot, "glow GlowTeam", () =>
            {
                glow.Glow.GlowTeam = -1;
                glow.Glow.GlowTeamUpdated();
            });
            Do(targetSlot, "glow GlowType", () =>
            {
                glow.Glow.GlowType = 3;
                glow.GlowUpdated();
            });

            if (pawn.AbsOrigin is { } glowPosition)
            {
                Do(targetSlot, "glow Teleport", () => glow.Teleport(glowPosition, null, null));
            }
            // The glow prop follows the RELAY, not the real player directly - this is the change
            // from the previous single-hop attempt.
            Do(targetSlot, "glow FollowEntity -> relay", () => glow.AcceptInput("FollowEntity", "!activator", relay, relay, 0));

            _relayEntityIndex[targetSlot] = relay.Index;
            _glowPropEntityIndex[targetSlot] = glow.Index;

            Do(targetSlot, "transmit state relay", () => ApplyTransmitStateForAllViewers((int)relay.Index));
            Do(targetSlot, "transmit state glow", () => ApplyTransmitStateForAllViewers((int)glow.Index));

            Step(targetSlot, $"done (relay={relay.Index}, glow={glow.Index})");
        });
    }

    /// <summary>Creates one link of the relay chain: spawn, model, and the collision/identity setup both links share.</summary>
    private CDynamicProp CreateGlowChainProp(string modelName)
    {
        Core.Logger.LogInformation("[CSRoll] XRAY prop: CreateEntityByDesignerName");
        var prop = Core.EntitySystem.CreateEntityByDesignerName<CDynamicProp>(GlowChainPropDesignerName);

        // Spawnflags configures the spawn process itself, so - unlike SetModel and the rest of
        // this setup - it must be set BEFORE DispatchSpawn. 256 is copied verbatim from the
        // original CS2-GameModifiers CSS plugin's proven glow-effect code; exact meaning
        // undocumented for this engine version.
        Core.Logger.LogInformation("[CSRoll] XRAY prop: Spawnflags + SpawnflagsUpdated (pre-spawn)");
        prop.Spawnflags = 256u;
        prop.SpawnflagsUpdated();

        // SetModel (and the rest of this configuration) must happen AFTER DispatchSpawn, or it
        // hits a Source 2 engine assertion ("SetupModel(): entity is still in the staging list")
        // and the model never actually gets set.
        Core.Logger.LogInformation("[CSRoll] XRAY prop: DispatchSpawn");
        using var keyValues = new CEntityKeyValues();
        prop.DispatchSpawn(keyValues);

        Core.Logger.LogInformation("[CSRoll] XRAY prop: SetModel");
        prop.SetModel(modelName);

        // Also copied verbatim from the original plugin's proven code - clears bit 2 of the
        // entity's own identity flags. Exact semantics undocumented; kept as an unexplained but
        // reproduced detail of a working reference rather than guessed at.
        Core.Logger.LogInformation("[CSRoll] XRAY prop: Identity.Flags raw write");
        if (prop.Identity is { } identity)
        {
            identity.Flags &= ~(uint)(1 << 2);
        }

        // Non-solid: these overlap the real player and must never physically collide with anyone.
        Core.Logger.LogInformation("[CSRoll] XRAY prop: Collision writes");
        prop.Collision.CollisionGroup = (byte)CollisionGroup.Nonphysical;
        prop.Collision.CollisionGroupUpdated();
        prop.Collision.SolidFlags = 4; // FSOLID_NOT_SOLID
        prop.Collision.SolidFlagsUpdated();
        prop.Collision.SolidType = SolidType_t.SOLID_NONE;
        prop.Collision.SolidTypeUpdated();

        Core.Logger.LogInformation("[CSRoll] XRAY prop: built ok");
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

    /// <summary>
    /// Bug fix: this despawned whatever entity currently sat at a remembered index, with no check
    /// that it was still one of ours. CS2 recycles entity indices aggressively, so once a stored
    /// index went stale - which the orphan path above used to guarantee - GetEntityByIndex handed
    /// back whatever had since been given that slot, and Despawn() destroyed it. That is a live
    /// player pawn, a weapon or the bomb being ripped out from under the engine, which is an
    /// immediate hard crash with nothing in the log. Verifying the designer name first means the
    /// worst a stale index can now do is nothing at all.
    /// </summary>
    private void DespawnTrackedProp(uint entityIndex, int slot, string role)
    {
        if (Core.EntitySystem.GetEntityByIndex(entityIndex) is not { IsValid: true } entity)
        {
            return;
        }

        if (entity.DesignerName != GlowChainPropDesignerName)
        {
            Core.Logger.LogWarning(
                "[CSRoll] XRAY slot {Slot}: refusing to despawn {Role} at recycled index {Index} - it is now a {DesignerName}, not one of ours.",
                slot, role, entityIndex, entity.DesignerName);
            return;
        }

        entity.Despawn();
    }

    protected void RemoveXrayFromSlot(int slot)
    {
        if (_relayEntityIndex.Remove(slot, out var relayIndex))
        {
            DespawnTrackedProp(relayIndex, slot, "relay");
        }

        if (_glowPropEntityIndex.Remove(slot, out var glowIndex))
        {
            DespawnTrackedProp(glowIndex, slot, "glow");
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
        // Bug fix: this used to remove the bookkeeping dictionary entries directly instead of
        // going through RemoveXrayFromSlot - the only method that actually despawns the relay/glow
        // CDynamicProp entities. A disconnecting glow target's props were never despawned and just
        // persisted, glowing, in the map for the rest of the round.
        RemoveXrayFromSlot(@event.PlayerId);
        CachedXrayEnabledSlots.Remove(@event.PlayerId);
        CSRollUtils.RevokeXrayVision(@event.PlayerId);
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
    // a global !rolltoggle (no specific assignment) still means everyone, same as before.
    protected override bool CheckEnableXray(IPlayer viewer) => IsAssignedTo(viewer.Slot);
}
