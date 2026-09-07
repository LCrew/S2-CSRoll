using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.EntitySystem;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Wallhack. Three independent mechanisms, each behind its own config switch:
///
/// 1. Radar spotting (Xray.RadarSpotting, ON) - every enemy is continuously marked as spotted for
///    the x-ray holder alone via CCSPlayerPawn.EntitySpottedState.SpottedByMask, a per-viewer
///    bitmask the engine already maintains. Per-viewer and exact, but radar contacts only.
///
/// 2. Pawn glow (Xray.GlowRealPawn, ON) - through-wall outlines written straight onto the real
///    pawns' CGlowProperty. Same class of operation as (1): no entities, nothing deferred. CS2 has
///    no per-viewer glow, so GlowTeam limits it to the holder's team and their teammates see it
///    too; that is the accepted price of a mechanism that does not crash.
///
/// 3. Glow props (Xray.GlowProps, OFF - KNOWN BROKEN) - a glowing duplicate of each target's model,
///    transmitted only to holders. This is the only way to get per-viewer outlines, and it has
///    never survived contact with a live server. Nine crashes across eight distinct engine calls.
///    After the rewrite below the fault moved from EXCEPTION_ACCESS_VIOLATION_EXEC at "ibute" to
///    EXCEPTION_ACCESS_VIOLATION_READ at 0xFFFFFFFFFFFFFFFF in the tick path, which means
///    GetEntityByIndex returned a wrapper that passed IsValid over memory the engine had already
///    freed. The engine deletes these props on its own and IsValid does not detect it, so there is
///    no guard that makes this safe from managed code. Left in, off, for anyone who wants to keep
///    digging - mechanism (2) is the supported way to get outlines.
///
/// The glow half was rebuilt from scratch after the original crashed the server at four
/// progressively later points. That original was ported from a CounterStrikeSharp plugin and
/// carried three things across that this codebase could never explain, all of which are now gone:
///
/// - A TWO-PROP RELAY CHAIN (pawn -> invisible relay -> visible glow) parented with
///   AcceptInput("FollowEntity"). Entity-I/O parenting is not documented in SwiftlyS2 at all, and
///   the final crash was inside that exact call: EXCEPTION_ACCESS_VIOLATION_EXEC jumping to
///   0x0000006574756269, an address whose bytes spell "ibute" - a virtual dispatch through a
///   pointer read out of string data. It reproduced at a byte-identical address across separate
///   runs, so not random heap reuse but a deterministic bad read, and it happened with BOTH props
///   verified IsValid on the line immediately before. No managed-side guard can prevent a fault
///   inside the engine's own input handler, so the call is gone rather than guarded. Position is
///   synced per tick with Teleport instead, which needs no parenting, no relay, and halves the
///   entity count. The old code's own comment says per-tick tracking was tried once and "didn't
///   behave as wanted" - almost certainly because of the modelless-spawn bug below, which would
///   have left it with no render bounds to draw.
///
/// - A MODELLESS SPAWN. DispatchSpawn was called with an empty CEntityKeyValues, and the engine
///   printed "prop_dynamic at (0.000, 0.000, 0.000) has no model name!" on every spawn for the
///   entire life of this feature. SetModel afterwards attaches a model but never re-runs the
///   spawn-time setup that builds render and collision bounds from it. Model and origin now go
///   through the spawn keyvalues, which is what the SwiftlyS2 docs show.
///
/// - A RAW WRITE clearing bit 2 of CEntityIdentity.m_flags, plus Spawnflags = 256, both copied
///   verbatim with their semantics undocumented by that code's own admission. m_flags is the
///   engine's per-entity lifecycle bookkeeping. Both are gone; Xray.ClearIdentityFlagBit2 can put
///   the identity write back for anyone who wants to test whether it was load-bearing.
///
/// Interaction with invisibility: SetupXray grants every holder's slot into CSRollUtils' shared
/// x-ray registry, which GameModifierInvisibleBase reads to exempt those viewers from its own
/// transmit block - so a Wallhack holder sees players hidden by ConditionalInvisibility/Vanish
/// too, not just their outline. That grant is independent of both switches above.
/// </summary>
public abstract class GameModifierXrayBase : GameModifierBase
{
    private static readonly Color TerroristGlowColor = new(255, 165, 0);
    private static readonly Color CounterTerroristGlowColor = new(135, 206, 235);
    private static readonly Color GlowPropRenderColor = new(1, 255, 255, 255);

    /// <summary>CGlowProperty.GlowType value for a through-wall outline.</summary>
    private const int GlowTypeOutline = 3;

    /// <summary>How far the outline stays visible, in world units. Comfortably past the length of any competitive map sightline.</summary>
    private const int GlowRangeUnits = 5000;

    /// <summary>The only classname this modifier ever creates - checked before anything is despawned, since CS2 recycles entity indices aggressively.</summary>
    private const string GlowChainPropDesignerName = "prop_dynamic";

    protected readonly HashSet<int> CachedXrayEnabledSlots = [];

    /// <summary>Target slot -> that target's glow prop entity index. One prop per target now, not two.</summary>
    private readonly Dictionary<int, uint> _glowPropEntityIndex = [];

    /// <summary>Slots whose prop build failed, so the per-tick loop does not retry it 64 times a second forever.</summary>
    private readonly HashSet<int> _glowPropBuildFailed = [];

    /// <summary>
    /// Ticks still to trace in full detail after activation.
    ///
    /// The schema probe in SetupXray comes back clean - every field resolves to a sane address with
    /// the right element count - and it runs BEFORE the first tick, which is where the server dies.
    /// So the fault is a specific call in the tick path, not a bad offset, and the only way to name
    /// it is to log every sub-step of the first few ticks and then go quiet. At 64 ticks a second
    /// this cannot stay on, hence the countdown.
    /// </summary>
    private int _traceTicksRemaining;

    /// <summary>Separates the field WRITE from the network-state notifier that follows it. Both cross into the engine and only one of them can be the one that faults.</summary>
    private void Trace(string step)
    {
        if (_traceTicksRemaining > 0)
        {
            Core.Logger.LogInformation("[CSRoll] XRAY tick trace: {Step}", step);
        }
    }

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
        _traceTicksRemaining = 3;
        Core.Event.OnTick += OnTick;
        SetupXray();
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= OnTick;

        // Both before CachedXrayEnabledSlots is cleared below - it is what they are keyed off.
        ClearRadarSpotting();
        ClearPawnGlow();

        foreach (var slot in _glowPropEntityIndex.Keys.ToList())
        {
            RemoveXrayFromSlot(slot);
        }

        _glowPropBuildFailed.Clear();

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

        Core.Logger.LogInformation(
            "[CSRoll] XRAY: {Count} viewer(s) granted x-ray. RadarSpotting={Radar}, GlowRealPawn={PawnGlow}, GlowProps={Glow}.",
            CachedXrayEnabledSlots.Count, Runtime.Config.Xray.RadarSpotting, Runtime.Config.Xray.GlowRealPawn, Runtime.Config.Xray.GlowProps);

        LogSchemaResolution();
    }

    /// <summary>
    /// Prints the addresses the schema fields this modifier writes actually resolve to, once per
    /// activation, for the first live pawn.
    ///
    /// This exists because the crash hunt reached a point where every managed guard was in place and
    /// the server still died reading 0xFFFFFFFFFFFFFFFF. That address is what an UNRESOLVED schema
    /// field looks like, and it means the question is no longer "which of my calls is wrong" but
    /// "do these fields resolve at all against the CS2 build this server is running". An address of
    /// 0 or -1 here says SwiftlyS2's schema data disagrees with the game binary, which no amount of
    /// plugin code can fix - the fix would be a SwiftlyS2 update.
    /// </summary>
    private void LogSchemaResolution()
    {
        foreach (var player in Core.PlayerManager.GetAlive())
        {
            if (player.PlayerPawn is not { } pawn || !CSRollUtils.IsUsableHandle(pawn))
            {
                continue;
            }

            var spotted = pawn.EntitySpottedState;
            var glow = pawn.Glow;

            Core.Logger.LogInformation(
                "[CSRoll] XRAY schema check on slot {Slot}: pawn=0x{Pawn:X} EntitySpottedState=0x{Spotted:X} (usable={SpottedOk}) SpottedByMask=0x{Mask:X} (usable={MaskOk}, count={Count}) Glow=0x{Glow:X} (usable={GlowOk})",
                player.Slot,
                pawn.Address,
                spotted.Address, CSRollUtils.IsUsableHandle(spotted),
                CSRollUtils.IsUsableHandle(spotted) ? spotted.SpottedByMask.Address : IntPtr.Zero,
                CSRollUtils.IsUsableHandle(spotted) && CSRollUtils.IsUsableHandle(spotted.SpottedByMask),
                CSRollUtils.IsUsableHandle(spotted) && CSRollUtils.IsUsableHandle(spotted.SpottedByMask) ? spotted.SpottedByMask.ElementCount : -1,
                glow.Address, CSRollUtils.IsUsableHandle(glow));

            return;
        }

        Core.Logger.LogInformation("[CSRoll] XRAY schema check: no live pawn to probe yet.");
    }

    protected virtual bool CheckEnableXray(IPlayer viewer) => false;

    private void OnTick()
    {
        if (CachedXrayEnabledSlots.Count == 0)
        {
            return;
        }

        Trace($"tick {4 - _traceTicksRemaining} begin");
        RefreshRadarSpotting();
        Trace("radar done");
        RefreshPawnGlow();
        Trace("pawn glow done");
        RefreshGlowProps();
        Trace($"tick {4 - _traceTicksRemaining} end - engine now processes the frame");

        if (_traceTicksRemaining > 0)
        {
            _traceTicksRemaining--;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Mechanism 3: glow on the real pawns
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Draws the through-wall outline on the REAL player pawns rather than on duplicate props.
    ///
    /// This is what GlowProps was trying to achieve, done without creating anything: CGlowProperty
    /// is written on pawns the engine already owns, exactly like RadarSpotting writes
    /// EntitySpottedState. No entity creation, no entity I/O, no despawn, no deferred callback -
    /// none of the surface that made the prop chain fail at eight different engine calls.
    ///
    /// The compromise is visibility. CS2 has no per-viewer glow; glow is a property of the entity,
    /// and GlowTeam is the finest filter the engine exposes, so the holder's teammates see the
    /// outlines too. That is accepted deliberately as the price of a mechanism that works. If
    /// holders span both teams there is no team value that would exclude anyone, so this refuses to
    /// draw rather than handing the whole server a wallhack.
    /// </summary>
    private void RefreshPawnGlow()
    {
        if (!Runtime.Config.Xray.GlowRealPawn)
        {
            return;
        }

        if (ResolveGlowAudienceTeam() is not { } audienceTeam)
        {
            return;
        }

        foreach (var target in Core.PlayerManager.GetAlive())
        {
            if (CachedXrayEnabledSlots.Contains(target.Slot) ||
                target.PlayerPawn is not { } pawn ||
                !CSRollUtils.IsUsableHandle(pawn))
            {
                continue;
            }

            Trace($"pawnglow: applying to slot {target.Slot}");
            ApplyPawnGlow(pawn, target, audienceTeam);
            Trace($"pawnglow: applied to slot {target.Slot}");
        }
    }

    /// <summary>
    /// Which team is allowed to see the outlines: the team every current holder is on. Null when
    /// there are no holders, or when they are split across both teams and no single GlowTeam value
    /// could exclude anybody.
    /// </summary>
    private Team? ResolveGlowAudienceTeam()
    {
        Team? team = null;

        foreach (var slot in CachedXrayEnabledSlots)
        {
            // Controller is the one handle on this path the schema probe never checked, and
            // IsValid alone would not reject an all-ones address on it either.
            Trace($"pawnglow: reading Controller for holder slot {slot}");
            if (Core.PlayerManager.GetPlayer(slot) is not { IsValid: true } holder ||
                holder.Controller is not { } controller ||
                !CSRollUtils.IsUsableHandle(controller))
            {
                continue;
            }

            if (team is null)
            {
                team = controller.Team;
                continue;
            }

            if (team != controller.Team)
            {
                // Holders on both sides - any GlowTeam value here would reveal outlines to players
                // who did not roll this. Drawing nothing is the correct call.
                return null;
            }
        }

        return team is Team.T or Team.CT ? team : null;
    }

    /// <summary>
    /// Writes the glow properties, but only when they are not already what we want. The engine
    /// resets these on respawn, so this is re-checked per tick rather than tracked in a set - and
    /// the equality check keeps it to a network update only on the frames something actually
    /// changed.
    /// </summary>
    private void ApplyPawnGlow(CCSPlayerPawn pawn, IPlayer target, Team audienceTeam)
    {
        TracePawnGlowStep("pawnglow: resolving Glow handle");
        var glow = pawn.Glow;
        if (!CSRollUtils.IsUsableHandle(glow))
        {
            return;
        }

        TracePawnGlowStep("pawnglow: reading current glow state");
        var color = target.Controller is { IsValid: true, Team: Team.T } ? TerroristGlowColor : CounterTerroristGlowColor;

        if (glow.GlowType == GlowTypeOutline && glow.GlowTeam == (int)audienceTeam && glow.GlowRange == GlowRangeUnits)
        {
            return;
        }

        // One notifier for one logical change, not five.
        //
        // This used to fire GlowColorOverrideUpdated, GlowRangeUpdated, GlowRangeMinUpdated and
        // GlowTeamUpdated on the subobject AND then GlowUpdated on the pawn - five overlapping
        // dirty-path notifications for a single edit. The live trace shows every one of those calls
        // returning cleanly and the whole tick completing, with the server dying only afterwards,
        // which points at the engine choking while it encodes the state we marked rather than at
        // any call we make. Marking the same region dirty by five different paths in one frame is
        // the most plausible way to do that. All the fields are written first, then the containing
        // subobject is marked changed exactly once.
        TracePawnGlowStep("pawnglow: writing glow fields");
        glow.GlowColorOverride = color;
        glow.GlowRange = GlowRangeUnits;
        glow.GlowRangeMin = 0;
        glow.GlowTeam = (int)audienceTeam;
        glow.GlowType = GlowTypeOutline;
        TracePawnGlowStep("pawnglow: GlowUpdated notifier");
        pawn.GlowUpdated();
        TracePawnGlowStep("pawnglow: GlowUpdated ok");
    }

    /// <summary>Trace hook usable from the static glow writer above.</summary>
    private void TracePawnGlowStep(string step) => Trace(step);

    /// <summary>Turns the pawn outlines back off. Players do not glow in normal play, so "restore" means "off" rather than a saved value.</summary>
    private void ClearPawnGlow()
    {
        foreach (var target in Core.PlayerManager.GetAllValidPlayers())
        {
            if (target.PlayerPawn is not { } pawn || !CSRollUtils.IsUsableHandle(pawn))
            {
                continue;
            }

            var glow = pawn.Glow;
            if (!CSRollUtils.IsUsableHandle(glow) || glow.GlowType == 0)
            {
                continue;
            }

            glow.GlowRange = 0;
            glow.GlowTeam = -1;
            glow.GlowType = 0;
            pawn.GlowUpdated();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Mechanism 1: radar spotting
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Marks every other alive player as spotted for the x-ray holders only, so they hold everyone
    /// on the radar through walls.
    ///
    /// Re-asserted every tick rather than set once: the engine recomputes real visibility each frame
    /// and clears bits it does not agree with. The already-set check keeps this to a network update
    /// only on the frames the engine actually cleared the bit.
    /// </summary>
    private void RefreshRadarSpotting()
    {
        if (!Runtime.Config.Xray.RadarSpotting)
        {
            return;
        }

        foreach (var target in Core.PlayerManager.GetAlive())
        {
            if (target.PlayerPawn is not { } pawn || !CSRollUtils.IsUsableHandle(pawn))
            {
                continue;
            }

            var changed = false;
            foreach (var viewerSlot in CachedXrayEnabledSlots)
            {
                if (viewerSlot != target.Slot)
                {
                    Trace($"radar: set bit viewer={viewerSlot} on slot {target.Slot}");
                    changed |= SetSpottedBit(pawn, viewerSlot, spotted: true);
                    Trace($"radar: bit set viewer={viewerSlot} changed={changed}");
                }
            }

            if (changed)
            {
                // Separated from the write above deliberately. The write lands at an address the
                // schema probe already verified; this is a native notifier that has to work out the
                // field's network path for itself, and is the more likely of the two to be where an
                // all-ones dereference comes from.
                Trace($"radar: SpottedByMaskUpdated on slot {target.Slot}");
                pawn.EntitySpottedState.SpottedByMaskUpdated();
                Trace($"radar: SpottedByMaskUpdated ok on slot {target.Slot}");
            }
        }
    }

    /// <summary>Takes back every bit this modifier set, so a holder losing x-ray stops seeing the radar contacts immediately.</summary>
    private void ClearRadarSpotting()
    {
        foreach (var target in Core.PlayerManager.GetAllValidPlayers())
        {
            if (target.PlayerPawn is not { } pawn || !CSRollUtils.IsUsableHandle(pawn))
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
    /// caller can skip the network update when nothing moved. Bounds-checked against the array's own
    /// ElementCount rather than an assumed length of two - a slot index past the end would be a raw
    /// out-of-bounds write into engine memory.
    /// </summary>
    private static bool SetSpottedBit(CCSPlayerPawn pawn, int viewerSlot, bool spotted)
    {
        // IsUsableHandle, not IsValid. An unresolved schema field still reports IsValid true - it
        // just carries an all-ones address - which is exactly how this path faulted at
        // 0xFFFFFFFFFFFFFFFF while every IsValid guard around it passed. See CSRollUtils.
        var spottedState = pawn.EntitySpottedState;
        if (!CSRollUtils.IsUsableHandle(spottedState))
        {
            return false;
        }

        var mask = spottedState.SpottedByMask;
        var word = viewerSlot / 32;
        if (!CSRollUtils.IsUsableHandle(mask) || viewerSlot < 0 || word >= mask.ElementCount)
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

    // ---------------------------------------------------------------------------------------
    // Mechanism 2: glow props
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a glow prop for every alive target that lacks one, keeps each existing one sitting on
    /// its target, and tears down the ones whose target has died or left.
    ///
    /// Driving this from the tick loop rather than from spawn/death hooks is what removes the whole
    /// class of bug the old implementation kept hitting: there is no deferred callback that can
    /// outlive a deactivation, no window between despawning an old prop and building its
    /// replacement, and no long-lived direct entity reference held across frames (which the
    /// SwiftlyS2 docs warn against). The prop is re-resolved from its index every tick and simply
    /// rebuilt if it has gone, which is also what makes it safe for the engine to delete one behind
    /// our back - the case the final minidump proved was happening.
    /// </summary>
    private void RefreshGlowProps()
    {
        if (!Runtime.Config.Xray.GlowProps)
        {
            return;
        }

        foreach (var slot in _glowPropEntityIndex.Keys.ToList())
        {
            if (Core.PlayerManager.GetPlayer(slot) is not { IsValid: true, IsAlive: true })
            {
                RemoveXrayFromSlot(slot);
            }
        }

        foreach (var target in Core.PlayerManager.GetAlive())
        {
            if (target.PlayerPawn is not { } pawn || !CSRollUtils.IsUsableHandle(pawn))
            {
                continue;
            }

            if (TryResolveGlowProp(target.Slot) is { } prop)
            {
                // Angles as well as origin: without them the duplicate keeps its spawn orientation
                // while the real player turns, which reads as a statue rather than a silhouette.
                if (pawn.AbsOrigin is { } origin)
                {
                    prop.Teleport(origin, pawn.AbsRotation, null);
                }

                continue;
            }

            if (!_glowPropBuildFailed.Contains(target.Slot))
            {
                TryBuildGlowProp(target, pawn);
            }
        }
    }

    /// <summary>
    /// Resolves this slot's glow prop, or null if it no longer exists. The designer-name check is
    /// what makes a recycled index harmless: CS2 reuses entity indices aggressively, so a remembered
    /// index can easily point at somebody's pawn or a weapon by the time it is read back - and
    /// despawning one of those is an instant crash.
    /// </summary>
    private CDynamicProp? TryResolveGlowProp(int slot)
    {
        if (!_glowPropEntityIndex.TryGetValue(slot, out var index))
        {
            return null;
        }

        var entity = Core.EntitySystem.GetEntityByIndex<CDynamicProp>(index);
        if (entity is not { IsValid: true } || entity.DesignerName != GlowChainPropDesignerName)
        {
            _glowPropEntityIndex.Remove(slot);
            return null;
        }

        return entity;
    }

    private void TryBuildGlowProp(IPlayer target, CCSPlayerPawn pawn)
    {
        var slot = target.Slot;

        var modelName = pawn.GetModel();
        if (string.IsNullOrEmpty(modelName) || pawn.AbsOrigin is not { } origin)
        {
            return;
        }

        var prop = Core.EntitySystem.CreateEntityByDesignerName<CDynamicProp>(GlowChainPropDesignerName);
        if (!prop.IsValid)
        {
            Core.Logger.LogWarning("[CSRoll] XRAY slot {Slot}: entity creation returned an invalid handle - no glow prop for this player.", slot);
            _glowPropBuildFailed.Add(slot);
            return;
        }

        // Model and origin go in as spawn keyvalues so the prop is fully set up the moment it
        // exists. Spawning it bare and calling SetModel afterwards is what produced
        // "prop_dynamic at (0.000, 0.000, 0.000) has no model name!" and left it with no render
        // bounds - which then crashed the server on the first Teleport once glow was enabled.
        using (var keyValues = new CEntityKeyValues())
        {
            keyValues.SetString("model", modelName);
            keyValues.SetVector("origin", origin);
            prop.DispatchSpawn(keyValues);
        }

        if (!prop.IsValid)
        {
            Core.Logger.LogWarning("[CSRoll] XRAY slot {Slot}: prop was destroyed during DispatchSpawn - no glow prop for this player.", slot);
            _glowPropBuildFailed.Add(slot);
            return;
        }

        if (Runtime.Config.Xray.ClearIdentityFlagBit2 && prop.Identity is { IsValid: true } identity)
        {
            identity.Flags &= ~(uint)(1 << 2);
        }

        // Non-solid: this overlaps the real player and must never physically collide with anyone.
        if (prop.Collision.IsValid)
        {
            prop.Collision.CollisionGroup = (byte)CollisionGroup.Nonphysical;
            prop.Collision.CollisionGroupUpdated();
            prop.Collision.SolidFlags = 4; // FSOLID_NOT_SOLID
            prop.Collision.SolidFlagsUpdated();
            prop.Collision.SolidType = SolidType_t.SOLID_NONE;
            prop.Collision.SolidTypeUpdated();
        }

        prop.Render = GlowPropRenderColor;
        prop.RenderUpdated();

        var glow = prop.Glow;
        if (!glow.IsValid)
        {
            Core.Logger.LogWarning("[CSRoll] XRAY slot {Slot}: Glow subobject is invalid (address={Address:X}) - despawning, no outline for this player.", slot, glow.Address);
            prop.Despawn();
            _glowPropBuildFailed.Add(slot);
            return;
        }

        // Same single-notifier rule as the pawn path above.
        glow.GlowColorOverride = target.Controller is { IsValid: true, Team: Team.T } ? TerroristGlowColor : CounterTerroristGlowColor;
        glow.GlowRange = GlowRangeUnits;
        glow.GlowRangeMin = 20;
        glow.GlowTeam = -1;
        glow.GlowType = GlowTypeOutline;
        prop.GlowUpdated();

        _glowPropEntityIndex[slot] = prop.Index;
        ApplyTransmitStateForAllViewers((int)prop.Index);

        Core.Logger.LogInformation("[CSRoll] XRAY slot {Slot}: glow prop built (index={Index}, model={Model}).", slot, prop.Index, modelName);
    }

    /// <summary>
    /// Explicitly sets transmit state in BOTH directions for every connected viewer: blocked for
    /// non-x-ray viewers, and explicitly un-blocked (not just left as a default) for x-ray-enabled
    /// viewers. The explicit un-block is what makes the prop visible to holders only.
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
        _glowPropBuildFailed.Remove(slot);

        if (TryResolveGlowProp(slot) is { } prop)
        {
            prop.Despawn();
        }

        _glowPropEntityIndex.Remove(slot);
    }

    private void OnClientConnected(IOnClientConnectedEvent @event)
    {
        if (!IsActive || Core.PlayerManager.GetPlayer(@event.PlayerId) is not { IsValid: true } viewer)
        {
            return;
        }

        // A newly connected viewer must be told about every currently glowing target individually.
        var isXrayEnabled = CachedXrayEnabledSlots.Contains(viewer.Slot);
        foreach (var entityIndex in _glowPropEntityIndex.Values)
        {
            viewer.ShouldBlockTransmitEntity((int)entityIndex, !isXrayEnabled);
        }
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
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
    // per-player random rounds, only that assigned player gets x-ray; a global !rolltoggle (no
    // specific assignment) still means everyone, same as before.
    protected override bool CheckEnableXray(IPlayer viewer) => IsAssignedTo(viewer.Slot);
}
