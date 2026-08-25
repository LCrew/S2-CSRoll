using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.EntitySystem;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Trace;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Zeus (taser) recharges much faster (not instantly) and hits at long range instead of its short
/// native reach.
///
/// Fast recharge: unlike CS:GO's "one use per life", CS2 actually recharges Zeus automatically via
/// the mp_taser_recharge_time server cvar (default 30s) - the same bolt-on-cvar ModifierConfig/*.cfg
/// mechanism used elsewhere in this codebase, just applied to MasterZeus.cfg.
///
/// Deliberately global, not per-player, despite SupportsPerPlayerRandomization: an earlier pass
/// decoupled the custom zap's cooldown into its own per-player config value specifically to stop
/// this cvar affecting everyone's native Zeus - but that also meant the assigned player's own Zeus
/// stopped getting the fast recharge, which was the actual point of the modifier. Reverted per
/// explicit instruction: the shared-cvar fast recharge for everyone is preferred over a
/// correctly-scoped recharge that no longer recharges fast at all.
///
/// Extended range: CS2's zeus attack is a short native trace with no exposed range field, so this
/// fires its own straight hitscan ray along the aim direction out to RangeDistance and zaps whoever
/// it strikes - the same thing the visible bolt depicts. The exact native "tased" stun/slow effect
/// field wasn't identified, so this applies a flat amount of damage as a best-effort stand-in.
///
/// Bug fix history: acquisition was previously an aim-cone search - iterate every living enemy, keep
/// those inside a cone around the aim direction, line-of-sight trace each, take the closest. It was
/// reported live as killing a player roughly 15 degrees off-aim, and the cone width could not simply
/// be tightened: the cone was measured to the target's AbsOrigin (feet), so from eye height a
/// perfectly-aimed shot read as increasingly off-aim the closer the target was (~17 degrees at 150
/// units, ~4.6 at 600). The wide cone existed to stop close-range zaps failing. A single ray has no
/// such range-dependent skew, needs no width tuning, and folds line-of-sight in for free - anything
/// that would have blocked the shot stops the ray first. Wallhack is still correctly blocked by
/// walls, which was the reason LOS was required in the first place.
///
/// The ray needs MaskTrace.Player to hit players at all (Solid alone traces world geometry and
/// passes through them) and the same func_buyzone veto the old LOS trace needed - without it the ray
/// dies on the spawn trigger volume, live-confirmed via HitEntity=func_buyzone.
///
/// Trigger mechanism: it's unconfirmed whether weapon_fire actually dispatches for a taser zap in
/// this engine build (a taser is a short native melee-range trace, not a bullet), so this hooks it
/// AND separately polls IPlayer.PressedButtons for Mouse1 (rising edge) every tick as a redundant,
/// lower-level fallback that doesn't depend on that event existing for this weapon type at all.
/// Both paths funnel through the same debounced entry point so a single real zap can't double-fire
/// if both signals happen to catch it.
///
/// Bug fix: the debounce used to be a fixed 0.2s, meant only to stop the SAME click double-firing
/// across both trigger paths above - not a real cooldown. A player could release and re-press
/// Mouse1 rapidly (each press is a fresh rising edge) and get a full zap on every single click, far
/// faster than the weapon's actual recharge. The debounce threshold now reads the live
/// mp_taser_recharge_time cvar value instead (confirmed via SwiftlyS2.CS2.dll metadata inspection:
/// Core.ConVar.Find&lt;float&gt;(name) returns IConVar&lt;float&gt; with a live .Value), so the same single
/// cvar governs both the native close-range recharge and this custom extended-range cooldown - one
/// source of truth, no separate config value that could drift out of sync. Damage was also bumped
/// from an arbitrary 15 to a config-tunable flat 200 (effectively a one-shot) per live feedback.
///
/// Also gives the assigned player(s) a Zeus automatically (on activation, and again on every spawn
/// since weapons reset each life/round) - previously this only enhanced a taser the player already
/// happened to own/buy, which meant actually getting to use the modifier depended on remembering to
/// buy one every round.
/// </summary>
public sealed class GameModifierMasterZeus : GameModifierBase
{
    private const string TaserDesignerName = "weapon_taser";
    private const string RechargeCvarName = "mp_taser_recharge_time";

    private readonly HashSet<int> _attackButtonWasDown = [];
    private readonly Dictionary<int, float> _lastZapTime = [];
    private readonly Dictionary<int, float> _lastLightningTime = [];
    private Guid _fireHookId;
    private Guid _spawnHookId;
    private bool _loggedCvarReadFailure;

    public GameModifierMasterZeus()
    {
        Name = "MasterZeus";
        Description = "Zeus recharges much faster and hits at very long range";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

    protected override void OnRegistered()
    {
        Core.Event.OnClientDisconnected += OnClientDisconnected;
        Core.Event.OnPrecacheResource += OnPrecacheResource;
    }

    protected override void OnUnregistered()
    {
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
        Core.Event.OnPrecacheResource -= OnPrecacheResource;
    }

    protected override void OnEnabled()
    {
        Core.Event.OnTick += OnTick;
        _fireHookId = Core.GameEvent.HookPost<EventWeaponFire>(OnWeaponFire);
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);

        foreach (var player in Core.PlayerManager.GetAlive())
        {
            if (IsAssignedTo(player.Slot))
            {
                GiveZeus(player);
            }
        }
    }

    protected override void OnDisabled()
    {
        Core.Event.OnTick -= OnTick;
        Core.GameEvent.Unhook(_fireHookId);
        Core.GameEvent.Unhook(_spawnHookId);
        _attackButtonWasDown.Clear();
        _lastZapTime.Clear();
        _lastLightningTime.Clear();
    }

    /// <summary>
    /// Bug fix history: the real native zap particle (particles/unified_weapon_fx/weapon_tracers_taser.vpcf,
    /// and its legacy pre-unified counterpart particles/weapons/cs_weapon_fx/weapon_tracers_taser.vpcf)
    /// is a composite CParticleSystemDefinition with an m_Children list and an m_hFallback chain -
    /// confirmed live, even after precaching every child/fallback path in that chain, that it never
    /// renders through ANY dispatch mechanism: not a manual info_particle_system spawn, and not
    /// IEngineService.DispatchParticleEffect either (which otherwise works fine for simple particles).
    /// Composite/wrapper particle definitions apparently can't be dispatched by name through either
    /// public API - likely only usable as an internal reference the native weapon-fire code expands
    /// at a lower level than plugins can reach. So LightningParticlePath must stay a simple,
    /// standalone particle, and only that one path needs precaching.
    /// </summary>
    private void OnPrecacheResource(IOnPrecacheResourceEvent @event)
    {
        @event.AddItem(Runtime.Config.MasterZeus.LightningParticlePath);
        if (!string.IsNullOrEmpty(Runtime.Config.MasterZeus.LightningSecondaryParticlePath))
        {
            @event.AddItem(Runtime.Config.MasterZeus.LightningSecondaryParticlePath);
        }
        // Unconditional (not gated behind Runtime.DebugMode) - this fires at map load, before there's
        // necessarily been a chance to !rolldebug for that particular load.
        Core.Logger.LogInformation("[CSRoll] MasterZeus precache-debug: AddItem({Path}) + secondary={Secondary}", Runtime.Config.MasterZeus.LightningParticlePath, Runtime.Config.MasterZeus.LightningSecondaryParticlePath);
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            GiveZeus(player);
        }

        return HookResult.Continue;
    }

    private static void GiveZeus(IPlayer player)
    {
        player.PlayerPawn?.ItemServices?.GiveItem(TaserDesignerName);
    }

    private void OnTick()
    {
        foreach (var player in Core.PlayerManager.GetAlive())
        {
            if (!IsAssignedTo(player.Slot) || player.PlayerPawn is not { } pawn ||
                pawn.WeaponServices?.ActiveWeapon.Value is not { } weapon || weapon.DesignerName != TaserDesignerName)
            {
                _attackButtonWasDown.Remove(player.Slot);
                continue;
            }

            var isDown = (player.PressedButtons & GameButtonFlags.Mouse1) != 0;

            if (isDown && _attackButtonWasDown.Add(player.Slot) && IsWeaponReadyToFire(weapon))
            {
                TryZapDebounced(player, pawn);
            }
            else if (!isDown)
            {
                _attackButtonWasDown.Remove(player.Slot);
            }
        }
    }

    /// <summary>
    /// Bug fix: the Mouse1 rising-edge path fires purely on button state, with no notion of whether
    /// the weapon can actually shoot - so swapping to the Zeus and clicking during its deploy
    /// ("pullout") animation produced a full zap, lightning and all, while the real weapon hadn't
    /// fired at all. NextPrimaryAttackTick is the tick the engine will next allow a primary attack,
    /// pushed into the future during deploy and between shots, so anything at or before the current
    /// tick means genuinely ready. The weapon_fire event path doesn't need this check - the engine
    /// only raises that event for shots it actually accepted.
    /// </summary>
    private bool IsWeaponReadyToFire(CBasePlayerWeapon weapon)
        => weapon.NextPrimaryAttackTick.Value <= Core.Engine.GlobalVars.TickCount;

    private HookResult OnWeaponFire(EventWeaponFire @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } shooter && @event.UserIdPawn is { } pawn &&
            pawn.WeaponServices?.ActiveWeapon.Value is { } weapon)
        {
            if (IsAssignedTo(shooter.Slot) && weapon.DesignerName == TaserDesignerName)
            {
                TryZapDebounced(shooter, pawn);
            }

            OnGlockFireLightningDebug(shooter.Slot, pawn, weapon);
        }

        return HookResult.Continue;
    }

    /// <summary>
    /// TEMPORARY debug aid (not a real feature): fires the real lightning chain on every Glock shot -
    /// no damage, no targeting/cooldown/assignment gating - so it can be visually confirmed
    /// independent of MasterZeus's own zap cooldown/targeting. Remove once the chain is confirmed
    /// looking right in-game and this is no longer needed as a separate test path.
    /// </summary>
    private void OnGlockFireLightningDebug(int shooterSlot, CCSPlayerPawn pawn, CBasePlayerWeapon weapon)
    {
        if (!Runtime.DebugMode || weapon.DesignerName != "weapon_glock" || pawn.EyePosition is not { } eyePosition)
        {
            return;
        }

        pawn.EyeAngles.ToDirectionVectors(out var forward, out _, out _);
        var origin = eyePosition + (forward * Runtime.Config.MasterZeus.MuzzleOffsetDistance);

        // Same buyzone/trigger exclusion TryExtendedRangeZap's LOS trace needs (func_buyzone etc.
        // register as an immediate hit despite being non-solid) - without it, testing near spawn
        // (where players usually are when firing a starting Glock) reported DidHit=true at fraction
        // ~0, i.e. Origin==Endpoint, self-blocking on the buyzone volume instead of tracing out.
        var traceParams = TraceParams.Builder(TraceParams.DefaultLine())
            .InteractWith(MaskTrace.Solid)
            .HitTrigger(false)
            .WithShouldHitEntity(entity => entity.DesignerName is not ("func_buyzone" or "func_bomb_target" or "func_hostage_rescue"))
            .IgnoreEntity(pawn)
            .Build();
        var traceEnd = origin + (forward * Runtime.Config.MasterZeus.RangeDistance);
        var result = Core.Trace.TraceShapeLine(origin, traceEnd, traceParams);
        // Bug fix: TraceResult.HitPoint is only populated when TraceResult.ExactHitPoint is true -
        // otherwise it reads as a zero Vector (world origin), which is exactly what showed up as the
        // chain's endpoint live. Fraction is always reliably computed as part of DidHit itself, so
        // interpolating along the trace segment with it is the reliable way to get the actual hit
        // position regardless of whether the exact hit point was separately calculated.
        var endpoint = origin + ((traceEnd - origin) * Math.Min(result.Fraction, 1f));

        SpawnLightningEffect(shooterSlot, GetLightningMuzzlePosition(eyePosition, pawn.EyeAngles), endpoint);
        Core.Logger.LogInformation("[CSRoll] MasterZeus glock-debug: fired lightning, Origin={Origin} Endpoint={Endpoint} DidHit={DidHit}", origin, endpoint, result.DidHit);
    }

    private void TryZapDebounced(IPlayer shooter, CCSPlayerPawn shooterPawn)
    {
        var now = Core.Engine.GlobalVars.CurrentTime;
        var cooldown = GetRechargeCooldownSeconds();

        if (_lastZapTime.TryGetValue(shooter.Slot, out var lastTime) && now - lastTime < cooldown)
        {
            LogZapDebug(shooter, $"blocked by cooldown ({now - lastTime:0.##}s of {cooldown:0.##}s elapsed)");
            return;
        }

        _lastZapTime[shooter.Slot] = now;
        LogZapDebug(shooter, $"attempt allowed (cooldown was {cooldown:0.##}s)");
        TryExtendedRangeZap(shooter, shooterPawn);
    }

    /// <summary>
    /// Reads mp_taser_recharge_time live via FindAsString/ValueAsString rather than the generic
    /// Find&lt;float&gt;(...) this used previously - that generic form requires guessing the cvar's exact
    /// native storage type, and a live test after switching to it showed the modifier still dealt
    /// zero damage at any range, so the type guess (or the generic path itself) can't be trusted.
    /// ValueAsString works for any cvar type without a guess, so this parses it as text instead - a
    /// strictly safer read that can't silently misbehave the same way.
    /// </summary>
    private float GetRechargeCooldownSeconds()
    {
        try
        {
            var raw = Core.ConVar.FindAsString(RechargeCvarName)?.ValueAsString;
            if (raw is not null && float.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }
        catch (Exception ex)
        {
            if (!_loggedCvarReadFailure)
            {
                _loggedCvarReadFailure = true;
                Core.Logger.LogWarning(ex, "[CSRoll] MasterZeus failed to read {Cvar} - falling back to a fixed {Fallback}s cooldown.", RechargeCvarName, Runtime.Config.MasterZeus.FallbackCooldownSeconds);
            }
        }

        return Runtime.Config.MasterZeus.FallbackCooldownSeconds;
    }

    /// <summary>
    /// Fires (LogInformation, gated behind !rolldebug so it stays silent otherwise) at every decision
    /// point in the extended-range zap - since two rounds of blind guesses at the cause of "deals no
    /// damage at any range" haven't fixed it, the next live test with !rolldebug on will show exactly
    /// which step is failing (no eye position, no qualifying target, or damage genuinely not landing)
    /// instead of guessing again.
    /// </summary>
    private void LogZapDebug(IPlayer shooter, string message)
    {
        if (Runtime.DebugMode)
        {
            Core.Logger.LogInformation("[CSRoll] MasterZeus ({Shooter}): {Message}", shooter.Controller is { IsValid: true } c ? c.PlayerName : shooter.Slot.ToString(), message);
        }
    }

    private void TryExtendedRangeZap(IPlayer shooter, CCSPlayerPawn shooterPawn)
    {
        if (shooterPawn.EyePosition is not { } eyePosition)
        {
            LogZapDebug(shooter, "aborted - no eye position resolved");
            return;
        }

        shooterPawn.EyeAngles.ToDirectionVectors(out var forward, out _, out _);

        // Bug fix: tracing straight from eyePosition starts the ray literally inside the shooter's
        // own head hitbox. TraceResult exposes a StartInSolid flag specifically for "the trace began
        // already embedded in solid geometry" - a very likely explanation for why LOS reported
        // blocked on every single attempt live, including a dead-on shot down a clearly open street:
        // a trace that starts inside solid typically reports DidHit=true immediately, regardless of
        // IgnoreEntity on anyone else. Nudging the start point forward, clear of the shooter's own
        // collision, before tracing toward the target avoids that self-block.
        var traceOrigin = eyePosition + (forward * Runtime.Config.MasterZeus.MuzzleOffsetDistance);

        // Target acquisition is a single straight hitscan along the aim direction - whoever the ray
        // actually strikes is the target, exactly like the visible bolt suggests.
        //
        // This replaced an aim-cone search (iterate every living enemy, keep those inside a cone of
        // the aim direction, LOS-trace each, take the closest). That was reported live as killing a
        // player roughly 15 degrees off-aim, and the width was hard to tune away: the cone was
        // measured to the target's feet, so a perfectly-aimed shot read as increasingly off-aim the
        // closer the target got (~17 degrees at 150 units), forcing a very wide cone to stay usable.
        // A ray has no such skew and needs no width tuning at all.
        //
        // It also folds the separate line-of-sight pass in: a ray that reaches a player necessarily
        // wasn't stopped by anything first. MaskTrace.Player is what makes players hittable at all -
        // MaskTrace.Solid alone traces world geometry and passes straight through them. The buyzone
        // veto is the same one the old LOS trace needed; without it the ray dies on the trigger volume
        // in spawn (live-confirmed via HitEntity=func_buyzone).
        var shotParams = TraceParams.Builder(TraceParams.DefaultLine())
            .InteractWith(MaskTrace.Solid | MaskTrace.Player)
            .HitTrigger(false)
            .WithShouldHitEntity(entity => entity.DesignerName is not ("func_buyzone" or "func_bomb_target" or "func_hostage_rescue"))
            .IgnoreEntity(shooterPawn)
            .Build();

        var shotEnd = traceOrigin + (forward * Runtime.Config.MasterZeus.RangeDistance);
        var shotResult = Core.Trace.TraceShapeLine(traceOrigin, shotEnd, shotParams);

        // Fraction is used rather than HitPoint - HitPoint is only populated when ExactHitPoint is
        // set and otherwise reads as world origin, which showed up live as bolts ending at a fixed
        // spot on the map.
        var lightningEndpoint = traceOrigin + ((shotEnd - traceOrigin) * Math.Min(shotResult.Fraction, 1f));

        IPlayer? bestTarget = null;
        if (shotResult.HitPlayer(out var hitPlayer) && hitPlayer is { IsValid: true } victim &&
            !CSRollUtils.IsSamePlayer(victim, shooter) && victim.Controller?.Team != shooter.Controller?.Team)
        {
            bestTarget = victim;
        }

        if (Runtime.DebugMode)
        {
            Core.Logger.LogInformation("[CSRoll] MasterZeus shot - DidHit={DidHit} Fraction={Fraction:0.###} HitEntity={Entity} Target={Target}",
                shotResult.DidHit, shotResult.Fraction, shotResult.Entity?.DesignerName ?? "null",
                bestTarget?.Controller is { IsValid: true } debugController ? debugController.PlayerName : "none");
        }

        // The bolt always renders, hit or miss - lightningEndpoint above is wherever the ray
        // actually stopped, so a whiff visibly reaches exactly as far as it looked like it should.
        SpawnLightningEffect(shooter.Slot, GetLightningMuzzlePosition(eyePosition, shooterPawn.EyeAngles), lightningEndpoint);

        if (bestTarget is null)
        {
            LogZapDebug(shooter, $"missed - ray stopped at fraction {shotResult.Fraction:0.###} on {shotResult.Entity?.DesignerName ?? "nothing"}");
            return;
        }

        var damage = Runtime.Config.MasterZeus.ZapDamage;
        bestTarget.TakeDamage(damage, DamageTypes_t.DMG_SHOCK, shooterPawn, shooterPawn);
        LogZapDebug(shooter, $"dealt {damage} damage to {(bestTarget.Controller is { IsValid: true } tc ? tc.PlayerName : bestTarget.Slot.ToString())}");
    }

    /// <summary>
    /// Renders a muzzle-to-target lightning visual on every extended-range zap, hit or miss.
    ///
    /// Bug fix history: IEngineService.DispatchParticleEffect looked like the right tool (it's the
    /// SDK's own documented particle API) but a live side-by-side isolation test (four variants fired
    /// off the same Glock shot) showed only a manually spawned info_particle_system entity actually
    /// rendering - not DispatchParticleEffect attached to a pawn, a weapon's muzzle_flash attachment,
    /// or a fresh helper entity. So this spawns a real info_particle_system entity: EffectName set,
    /// StartActive true, DispatchSpawn, Teleport, AcceptInput("Start") - this exact combination is
    /// what's confirmed live to render, contrary to the EffectIndex.IsValid diagnostic (which read
    /// false even on a working spawn - not a reliable render-readiness signal in this SDK build).
    ///
    /// Only simple, non-composite particles work this way - the real native zap particle
    /// (particles/unified_weapon_fx/weapon_tracers_taser.vpcf) is a composite CParticleSystemDefinition
    /// with an m_Children/m_hFallback chain and was confirmed live to never render through any
    /// dispatch mechanism, so LightningParticlePath must be one of its standalone children instead -
    /// weapon_tracers_taser_wire1a.vpcf (the actual wire/bolt visual) is what's currently configured.
    ///
    /// Originally approximated a two-point beam as a chain of individually-spawned points, since
    /// DispatchParticleEffect (which can't express a raw two-point stretch, only attach-to-entity) was
    /// believed to be the only working mechanism - once manual entity spawn was confirmed working
    /// instead, that chain was unnecessary complexity: wire1a's own C_INIT_CreateSequentialPathV2
    /// initializer already distributes its emitted particles along a path from control point 0 to
    /// control point 1 in a single spawn. So this now spawns one real muzzle-to-target line per
    /// strand: Teleport sets CP0 (the entity's own position), and data_cp=1/data_cp_value (passed
    /// through DispatchSpawn's keyvalues, the same CS2Fixes-proven mechanism) sets CP1 to the target -
    /// bug fix, confirmed live: CP1 defaults to raw world origin (0,0,0) on a freshly spawned entity
    /// if left unset (the .vpcf's own m_controlPointConfigurations showing CP1 defaulting to "self" is
    /// Source2Viewer/Hammer editor-preview-only, not applied at runtime), so leaving it unset made the
    /// wire's far end jump to the map's true origin instead of the intended target.
    ///
    /// LightningStrandCount spawns multiple copies of the same full muzzle-to-target line for a
    /// denser/more intense look. Live testing showed identical overlapping copies just look like one
    /// weak strand, so strands beyond the first get a small random perpendicular jitter
    /// (LightningStrandJitterDistance) so they read as a bundle of distinct bolts instead. Also
    /// layers LightningSecondaryParticlePath (wire1b, a sibling of wire1a in the real taser tracer's
    /// m_Children list) on every strand for a denser combined visual, and passes tint_cp/tint_cp_color
    /// keyvalues (the same mechanism CS2Fixes uses for tracer color) with a bright value as a
    /// brightness experiment - not confirmed to affect this specific particle's rendering, but a
    /// harmless one to try since wire1a's own C_INIT_RandomColor uses a fairly dim base tint range.
    ///
    /// Bug fix (arc/rotation): Teleport was passing null for angles, leaving the entity at world
    /// (0,0,0) orientation regardless of shot direction - wire1a's C_INIT_CreateSequentialPathV2 has
    /// a nonzero m_flBulge, and if its perpendicular bulge offset is computed relative to the
    /// entity's own local axes rather than purely from CP0/CP1 world positions, a fixed world
    /// orientation would produce a consistent visual arc that gets more pronounced the more the shot
    /// direction diverges from world-forward - exactly the "always arcing right" symptom reported.
    /// Orienting the entity to face the target instead is a reasonable fix to try, though not fully
    /// certain without deeper visibility into the operator's internal bulge-axis calculation.
    ///
    /// All strand/secondary entities are despawned on a timer shortly after so repeated zaps don't
    /// leak entities.
    /// </summary>
    /// <summary>
    /// Visual start point for the bolt - offset from the eye along the shooter's own axes to sit
    /// roughly where the weapon's barrel is, rather than dead centre of their view at eye height.
    /// Kept separate from the gameplay trace origin (MuzzleOffsetDistance) so tuning the look can't
    /// affect targeting. Necessarily an approximation: the SDK exposes no attachment/bone position
    /// query, so the model's real muzzle_flash point can't be read.
    /// </summary>
    private Vector GetLightningMuzzlePosition(Vector eyePosition, QAngle eyeAngles)
    {
        eyeAngles.ToDirectionVectors(out var forward, out var right, out var up);

        return eyePosition
            + (forward * Runtime.Config.MasterZeus.LightningMuzzleForwardOffset)
            + (right * Runtime.Config.MasterZeus.LightningMuzzleRightOffset)
            + (up * Runtime.Config.MasterZeus.LightningMuzzleUpOffset);
    }

    private void SpawnLightningEffect(int shooterSlot, Vector origin, Vector target)
    {
        // Spam backstop, deliberately enforced here rather than at any single call site so every
        // trigger path is covered - the real zap is already gated well above this by
        // mp_taser_recharge_time, but an ungated caller firing at weapon rate would otherwise spawn
        // (segments x strands x 2) entities per shot and flood the entity list.
        var cooldown = Runtime.Config.MasterZeus.LightningCooldownSeconds;
        var now = Core.Engine.GlobalVars.CurrentTime;
        if (cooldown > 0f && _lastLightningTime.TryGetValue(shooterSlot, out var lastTime) && now - lastTime < cooldown)
        {
            return;
        }

        _lastLightningTime[shooterSlot] = now;

        var direction = (target - origin).Normalized();
        var angles = direction.ToQAngles();
        angles.ToDirectionVectors(out _, out var right, out var up);

        var strandCount = Math.Max(1, Runtime.Config.MasterZeus.LightningStrandCount);
        var segments = Math.Max(1, Runtime.Config.MasterZeus.LightningChainSegments);
        var jitterDistance = Runtime.Config.MasterZeus.LightningStrandJitterDistance;
        var secondaryPath = Runtime.Config.MasterZeus.LightningSecondaryParticlePath;
        var entityIndexes = new List<uint>(strandCount * segments * 2);

        for (var strand = 0; strand < strandCount; strand++)
        {
            var jitter = strand == 0
                ? default(Vector)
                : (right * ((float)(Random.Shared.NextDouble() * 2 - 1) * jitterDistance)) +
                  (up * ((float)(Random.Shared.NextDouble() * 2 - 1) * jitterDistance));

            var strandOrigin = origin + jitter;
            var strandTarget = target + jitter;
            var strandDelta = strandTarget - strandOrigin;

            // Split into segments so each particle's own path bulge (a fraction of ITS path length,
            // not an absolute distance) stays small - see LightningChainSegments for the full reason.
            for (var i = 0; i < segments; i++)
            {
                var segmentStart = strandOrigin + (strandDelta * ((float)i / segments));
                var segmentEnd = strandOrigin + (strandDelta * ((float)(i + 1) / segments));

                SpawnLightningParticle(Runtime.Config.MasterZeus.LightningParticlePath, segmentStart, segmentEnd, entityIndexes);

                if (!string.IsNullOrEmpty(secondaryPath))
                {
                    SpawnLightningParticle(secondaryPath, segmentStart, segmentEnd, entityIndexes);
                }
            }
        }

        Core.Scheduler.DelayBySeconds(Runtime.Config.MasterZeus.LightningLifetimeSeconds, () =>
        {
            foreach (var entityIndex in entityIndexes)
            {
                if (Core.EntitySystem.GetEntityByIndex(entityIndex) is { IsValid: true } spawned)
                {
                    spawned.Despawn();
                }
            }
        });
    }

    private void SpawnLightningParticle(string particlePath, Vector origin, Vector target, List<uint> entityIndexes)
    {
        var particle = Core.EntitySystem.CreateEntityByDesignerName<CParticleSystem>("info_particle_system");
        particle.EffectName = particlePath;
        particle.StartActive = true;

        // Empty but deliberately non-null - this is the exact spawn recipe confirmed to render, and
        // the tint_cp/tint_cp_color keyvalues that used to live here were removed after live testing
        // showed no colour combination had any effect (the bolt's colour comes from the particle's
        // material, not its colour initializer, so it isn't changeable server-side).
        using (var keyValues = new CEntityKeyValues())
        {
            particle.DispatchSpawn(keyValues);
        }

        // Angle left as null (leave the entity's own spawn angle) - both this and an explicit
        // default(QAngle) were tested live and null looked better.
        particle.Teleport(origin, null, null);

        // Bug fix (endpoint): DataCP/DataCPValue is NOT the mechanism that drives this particle's
        // control point 1. Both the absolute world target and the origin-relative delta were tested
        // live and produced the same wrong result (endpoint stranded far away in the sky), which rules
        // out a coordinate-space mismatch as the explanation and points at the mechanism itself -
        // that pair is a generic "data" channel a particle definition has to explicitly opt into
        // reading, and wire1a simply doesn't. Its C_INIT_CreateSequentialPathV2 reads real control
        // point 1 instead, whose server-side override channel is ServerControlPoints -
        // ISchemaFixedArray<Vector> with a ref indexer, so writes land in the entity - paired with
        // ServerControlPointAssignments, which maps each of the 4 slots to a control point index
        // (255 = unassigned) rather than slot index implying CP index. All four slots are written
        // explicitly here (slot 0 -> CP0 = muzzle, slot 1 -> CP1 = target, slots 2/3 -> unassigned)
        // so nothing is left to an unknown default, then both Updated() calls propagate to clients.
        //
        // This mechanism was tried once very early on and dismissed, but that judgement was made
        // while the composite parent particle meant NOTHING rendered at all, so it was never actually
        // ruled out - the dismissal was based on bad evidence.
        particle.ServerControlPointAssignments[0] = 0;
        particle.ServerControlPoints[0] = origin;
        particle.ServerControlPointAssignments[1] = 1;
        particle.ServerControlPoints[1] = target;
        particle.ServerControlPointAssignments[2] = 255;
        particle.ServerControlPointAssignments[3] = 255;
        particle.ServerControlPointsUpdated();
        particle.ServerControlPointAssignmentsUpdated();

        particle.AcceptInput("Start", "", null, null, 0);

        entityIndexes.Add(particle.Index);
    }

    /// <summary>Bug fix: _attackButtonWasDown/_lastZapTime were only ever cleared in OnDisabled - a mid-round disconnect left stale entries a reconnecting player into the same slot could briefly inherit.</summary>
    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _attackButtonWasDown.Remove(@event.PlayerId);
        _lastZapTime.Remove(@event.PlayerId);
        _lastLightningTime.Remove(@event.PlayerId);
    }
}
