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
            if (target.PlayerPawn is not { IsValid: true } pawn)
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
            if (target.PlayerPawn is not { IsValid: true } pawn)
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
        // EntitySpottedState is a nested schema subobject and SpottedByMask a fixed array inside it -
        // both INativeHandle, both checked. This runs every tick for every alive player, so an
        // unresolved offset here would be a raw write into engine memory 64 times a second.
        var spottedState = pawn.EntitySpottedState;
        if (!spottedState.IsValid)
        {
            return false;
        }

        var mask = spottedState.SpottedByMask;
        var word = viewerSlot / 32;
        if (!mask.IsValid || viewerSlot < 0 || word >= mask.ElementCount)
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

    /// <summary>
    /// Do(), plus a liveness re-check of the entities the step is about to touch.
    ///
    /// The live minidump is a use-after-free - one of the glow-chain props is destroyed between
    /// being built and being used, and the engine then makes a virtual call through what is by then
    /// string data. Checking IsValid once at creation cannot catch that; the handles have to be
    /// re-tested at each point of use, which is what this does. A dead handle aborts the step and
    /// names itself rather than being handed to the engine.
    /// </summary>
    private bool DoChecked(int slot, string step, INativeHandle a, INativeHandle b, Action call)
    {
        if (!a.IsValid || !b.IsValid)
        {
            Core.Logger.LogWarning(
                "[CSRoll] XRAY slot {Slot}: entity DESTROYED before '{Step}' (first={FirstValid}, second={SecondValid}) - aborting the build.",
                slot, step, a.IsValid, b.IsValid);
            return false;
        }

        Do(slot, step, call);
        return true;
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
            if (currentTarget is not { IsValid: true, IsAlive: true } || currentTarget.PlayerPawn is not { IsValid: true } pawn)
            {
                return;
            }

            // PlayerPawn being non-null only says the wrapper exists, not that it points at a live
            // entity. This pawn is about to be read for its model and handed to the engine twice as
            // an AcceptInput activator/caller, so it gets the same INativeHandle check as everything
            // else on this path.
            if (!pawn.IsValid)
            {
                Core.Logger.LogWarning("[CSRoll] XRAY slot {Slot}: pawn is non-null but INVALID - aborting build.", targetSlot);
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
            var relay = CreateGlowChainProp(modelName, pawn.AbsOrigin);
            if (relay is null)
            {
                return;
            }

            Step(targetSlot, "create glow prop");
            var glow = CreateGlowChainProp(modelName, pawn.AbsOrigin);
            if (glow is null)
            {
                // The relay is already standing at this point - take it back down rather than
                // stranding a prop nothing tracks (nothing has been written to the index
                // dictionaries yet, so teardown would never find it).
                Core.Logger.LogWarning("[CSRoll] XRAY slot {Slot}: glow prop failed to build - despawning the orphaned relay.", targetSlot);
                relay.Despawn();
                return;
            }

            // Resolved once and validated before any of the five writes below. Glow is the subobject
            // the live crash is currently sitting on top of, and it is precisely the kind of nested
            // schema field whose offset can fail to resolve against a newer CS2 build while the
            // wrapper still hands back a usable-looking object.
            var glowProperties = glow.Glow;
            if (!glowProperties.IsValid)
            {
                Core.Logger.LogWarning(
                    "[CSRoll] XRAY slot {Slot}: Glow subobject is INVALID (address={Address:X}) - skipping all Glow writes. The outline will not render; this is the write that would have corrupted memory.",
                    targetSlot, glowProperties.Address);
                relay.Despawn();
                glow.Despawn();
                return;
            }

            Do(targetSlot, "relay RenderMode", () =>
            {
                relay.RenderMode = RenderMode_t.kRenderNone;
                relay.RenderModeUpdated();
            });

            if (pawn.AbsOrigin is { } relayPosition)
            {
                if (!DoChecked(targetSlot, "relay Teleport", relay, pawn, () => relay.Teleport(relayPosition, null, null))) return;
            }

            if (!DoChecked(targetSlot, "relay FollowEntity -> pawn", relay, pawn, () => relay.AcceptInput("FollowEntity", "!activator", pawn, pawn, 0))) return;

            Do(targetSlot, "glow Render", () =>
            {
                glow.Render = GlowPropRenderColor;
                glow.RenderUpdated();
            });

            // Moved AHEAD of the Glow.* writes, which is where it used to sit. The live crash landed
            // on exactly this call with "ok glow GlowType" logged immediately before it: once
            // GlowType and GlowRange are live the prop is on the glow render path, and moving it then
            // triggers a transform update that reads render bounds. Positioning it while it is still
            // an inert prop and only switching glow on afterwards means the one ordering confirmed
            // to kill the server cannot happen again, whatever else is wrong.
            //
            // Belt and braces with the spawn-keyvalue fix in CreateGlowChainProp, which is the real
            // cause - the prop now arrives with a model and valid bounds, so this update has
            // something to read. Both are kept: the reorder costs nothing and this is not a call
            // worth being wrong about twice.
            if (pawn.AbsOrigin is { } glowPosition)
            {
                if (!DoChecked(targetSlot, "glow Teleport", glow, relay, () => glow.Teleport(glowPosition, null, null))) return;
            }

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
                glowProperties.GlowColorOverride = currentTarget.Controller?.Team == Team.T ? TerroristGlowColor : CounterTerroristGlowColor;
                glowProperties.GlowColorOverrideUpdated();
            });
            Do(targetSlot, "glow GlowRange", () =>
            {
                glowProperties.GlowRange = 5000;
                glowProperties.GlowRangeUpdated();
            });
            Do(targetSlot, "glow GlowRangeMin", () =>
            {
                glowProperties.GlowRangeMin = 20;
                glowProperties.GlowRangeMinUpdated();
            });
            Do(targetSlot, "glow GlowTeam", () =>
            {
                glowProperties.GlowTeam = -1;
                glowProperties.GlowTeamUpdated();
            });
            Do(targetSlot, "glow GlowType", () =>
            {
                glowProperties.GlowType = 3;
                glow.GlowUpdated();
            });

            // The glow prop follows the RELAY, not the real player directly - this is the change
            // from the previous single-hop attempt.
            //
            // Re-validated immediately before the call rather than trusting the checks made at build
            // time. The minidump for this exact step is EXCEPTION_ACCESS_VIOLATION_EXEC at
            // 0x6574756269 - the bytes of that address spell "ibute" - which is a virtual call
            // through a vtable pointer that has been overwritten by string data. That is a
            // use-after-free: one of these two props was destroyed between being built and being
            // used here, and its memory reused. Validity at creation says nothing about validity
            // several engine calls later, so both handles are re-checked at the point of use and the
            // dead one is named.
            if (!relay.IsValid || !glow.IsValid)
            {
                Core.Logger.LogWarning(
                    "[CSRoll] XRAY slot {Slot}: a glow-chain prop was DESTROYED before parenting (relayValid={RelayValid}, glowValid={GlowValid}) - aborting. This is the use-after-free the minidump caught.",
                    targetSlot, relay.IsValid, glow.IsValid);

                if (relay.IsValid)
                {
                    relay.Despawn();
                }

                if (glow.IsValid)
                {
                    glow.Despawn();
                }

                return;
            }

            Do(targetSlot, "glow FollowEntity -> relay", () => glow.AcceptInput("FollowEntity", "!activator", relay, relay, 0));

            _relayEntityIndex[targetSlot] = relay.Index;
            _glowPropEntityIndex[targetSlot] = glow.Index;

            if (!DoChecked(targetSlot, "transmit state relay", relay, glow, () => ApplyTransmitStateForAllViewers((int)relay.Index))) return;
            if (!DoChecked(targetSlot, "transmit state glow", relay, glow, () => ApplyTransmitStateForAllViewers((int)glow.Index))) return;

            Step(targetSlot, $"done (relay={relay.Index}, glow={glow.Index})");
        });
    }

    /// <summary>Creates one link of the relay chain: spawn, model, and the collision/identity setup both links share.</summary>
    private CDynamicProp? CreateGlowChainProp(string modelName, Vector? spawnOrigin)
    {
        Core.Logger.LogInformation("[CSRoll] XRAY prop: CreateEntityByDesignerName");
        var prop = Core.EntitySystem.CreateEntityByDesignerName<CDynamicProp>(GlowChainPropDesignerName);

        // Every schema wrapper in SwiftlyS2 derives from INativeHandle, which carries IsValid and
        // Address - and until now this whole path checked neither, anywhere. A schema subobject
        // resolves to "entity address + field offset", so if the entity failed to create, or a field
        // offset failed to resolve against the running CS2 build, the wrapper still hands back a
        // perfectly ordinary-looking object pointing at an address that is not the field. Writing
        // through it is a raw write into whatever happens to live there, which is memory corruption
        // in the host process - not something a try/catch can ever see, and the exact profile of a
        // crash with no managed exception and no dump. The only place it can be stopped is before
        // the call, so every hop is checked from here on.
        if (!prop.IsValid)
        {
            Core.Logger.LogWarning("[CSRoll] XRAY prop: CreateEntityByDesignerName returned an INVALID entity - aborting build.");
            return null;
        }

        // Spawnflags configures the spawn process itself, so - unlike SetModel and the rest of
        // this setup - it must be set BEFORE DispatchSpawn. 256 is copied verbatim from the
        // original CS2-GameModifiers CSS plugin's proven glow-effect code; exact meaning
        // undocumented for this engine version.
        Core.Logger.LogInformation("[CSRoll] XRAY prop: Spawnflags + SpawnflagsUpdated (pre-spawn)");
        prop.Spawnflags = 256u;
        prop.SpawnflagsUpdated();

        // Bug fix, and the actual cause of the live crash: this used to DispatchSpawn with an EMPTY
        // CEntityKeyValues, and the engine said so on every single spawn -
        //     prop_dynamic at (0.000, 0.000, 0.000) has no model name!
        // - a line that was in the console the whole time and went unread. The prop was being
        // brought into the world with no model and no origin, and SetModel below then attached one
        // after the fact, which sets the model but never re-runs the spawn-time setup that builds
        // the entity's render and collision bounds from it.
        //
        // That is survivable right up until something asks for those bounds. The relay prop is
        // kRenderNone with no glow and teleports perfectly happily; the glow prop, once GlowType and
        // GlowRange are live, crashes the server on its very next Teleport - the transform update
        // walks a render/glow path that reads model bounds which were never built. Hence the fault
        // landing on "glow Teleport" with "ok glow GlowType" immediately before it.
        //
        // Passing model (and origin) through the spawn keyvalues is the normal Source 2 way to do
        // this and means the prop is fully set up by the time it exists, rather than patched up
        // afterwards. This is separate from the SetModel-after-DispatchSpawn rule noted below: that
        // is about the SetModel CALL, not about the spawn keyvalue.
        Core.Logger.LogInformation("[CSRoll] XRAY prop: DispatchSpawn (model={Model})", modelName);
        using var keyValues = new CEntityKeyValues();
        keyValues.SetString("model", modelName);
        if (spawnOrigin is { } origin)
        {
            keyValues.SetVector("origin", origin);
        }

        prop.DispatchSpawn(keyValues);

        Core.Logger.LogInformation("[CSRoll] XRAY prop: SetModel");
        prop.SetModel(modelName);

        // Also copied verbatim from the original plugin's proven code - clears bit 2 of the
        // entity's own identity flags. Exact semantics undocumented; kept as an unexplained but
        // reproduced detail of a working reference rather than guessed at.
        // Now OFF by default, and the prime suspect for the use-after-free.
        //
        // CEntityIdentity.m_flags is the engine's own per-entity lifecycle bookkeeping - it is where
        // "marked for deletion", "spawned", "is in a list" style bits live. This line clears bit 2 of
        // it and was copied verbatim from the original CSS plugin with, by its own admission, no idea
        // what the bit means on this engine version. Blindly clearing a lifecycle bit on an entity
        // and then being surprised when that entity is destroyed underneath you is not a coincidence
        // worth defending, and the minidump says one of these props is being freed between build and
        // use. So it goes behind a switch, default off, rather than staying in as an unexplained
        // write to memory that decides whether the object continues to exist.
        if (Runtime.Config.Xray.ClearIdentityFlagBit2)
        {
            Core.Logger.LogInformation("[CSRoll] XRAY prop: Identity.Flags raw write");
            if (prop.Identity is { IsValid: true } identity)
            {
                identity.Flags &= ~(uint)(1 << 2);
            }
        }

        // Non-solid: these overlap the real player and must never physically collide with anyone.
        Core.Logger.LogInformation("[CSRoll] XRAY prop: Collision writes");
        var collision = prop.Collision;
        if (!collision.IsValid)
        {
            Core.Logger.LogWarning("[CSRoll] XRAY prop: Collision subobject is INVALID - skipping collision writes (prop will be solid).");
        }
        else
        {
            collision.CollisionGroup = (byte)CollisionGroup.Nonphysical;
            collision.CollisionGroupUpdated();
            collision.SolidFlags = 4; // FSOLID_NOT_SOLID
            collision.SolidFlagsUpdated();
            collision.SolidType = SolidType_t.SOLID_NONE;
            collision.SolidTypeUpdated();
        }

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
