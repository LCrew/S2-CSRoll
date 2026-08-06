using Microsoft.Extensions.Logging;

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
/// Bug fix: "fast recharge" used to be a bolt-on ModifierConfig/MasterZeus.cfg setting the
/// mp_taser_recharge_time server cvar (same mechanism KnivesOnly.cfg uses for mp_buy_allow_guns) -
/// but unlike KnivesOnly (which is deliberately global, SupportsPerPlayerRandomization=false),
/// MasterZeus IS per-player-randomizable, so activating it for one player was quietly speeding up
/// literally every connected player's own separately-bought Zeus recharge too. There's no known
/// per-client equivalent of that cvar, so rather than keep a fast-but-global native recharge, this
/// custom extended-range zap ability now has its own dedicated, config-driven cooldown
/// (Config.MasterZeus.ZapCooldownSeconds) completely independent of mp_taser_recharge_time - the
/// assigned player's native Zeus still recharges at the normal server-wide rate, but their custom
/// zap (which doesn't consume the weapon's native charge at all - see TryExtendedRangeZap) is
/// available on its own much shorter schedule, unaffected by and not affecting anyone else.
///
/// Extended range: CS2's zeus attack is a short native trace with no exposed range field, so target
/// resolution here iterates every living enemy directly - within range, within a generous aim cone
/// of the shooter's eye direction, and with a plain solid-only line-of-sight check (IgnoreEntity on
/// both shooter and candidate pawns) - picking the closest qualifying target. The exact native
/// "tased" stun/slow effect field wasn't identified - this applies a moderate amount of damage as a
/// best-effort stand-in.
///
/// Bug fix history: this traces/aims against candidatePawn.AbsOrigin (feet-level), which is the
/// exact configuration live-confirmed working (back when this only dealt 15 damage - LOS correctly
/// let real ranged hits through while still blocking on real obstructions). A later change swapped
/// this to candidatePawn.EyePosition on a theory that an eye-to-feet trace clips the ground at long
/// range - live !debug testing showed that swap made LOS reject 100% of attempts, including a
/// dead-on shot down a clearly open street, so it was a regression, not a fix, and has been reverted
/// back to AbsOrigin. (LOS being required at all is deliberate: Wallhack should still be blocked by
/// walls even with an extended-range zap - removing the check entirely was tried and immediately
/// reverted since it let MasterZeus shoot through the whole map.)
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
/// faster than intended. The debounce threshold is now Config.MasterZeus.ZapCooldownSeconds (see the
/// class-level bug-fix note above for why this moved off the shared mp_taser_recharge_time cvar).
/// Damage was also bumped from an arbitrary 15 to a config-tunable flat 200 (effectively a one-shot)
/// per live feedback.
///
/// Also gives the assigned player(s) a Zeus automatically (on activation, and again on every spawn
/// since weapons reset each life/round) - previously this only enhanced a taser the player already
/// happened to own/buy, which meant actually getting to use the modifier depended on remembering to
/// buy one every round.
/// </summary>
public sealed class GameModifierMasterZeus : GameModifierBase
{
    private const float ExtendedRangeDistance = 4000f;
    private const float AimConeCosine = 0.85f; // ~roughly a 60-degree full cone, generous aim tolerance
    private const float MuzzleOffsetDistance = 24f; // clears the shooter's own head hitbox before the LOS trace starts
    private const string TaserDesignerName = "weapon_taser";

    private readonly HashSet<int> _attackButtonWasDown = [];
    private readonly Dictionary<int, float> _lastZapTime = [];
    private Guid _fireHookId;
    private Guid _spawnHookId;

    public GameModifierMasterZeus()
    {
        Name = "MasterZeus";
        Description = "Zeus recharges much faster and hits at very long range";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
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

            if (isDown && _attackButtonWasDown.Add(player.Slot))
            {
                TryZapDebounced(player, pawn);
            }
            else if (!isDown)
            {
                _attackButtonWasDown.Remove(player.Slot);
            }
        }
    }

    private HookResult OnWeaponFire(EventWeaponFire @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } shooter && IsAssignedTo(shooter.Slot) &&
            @event.UserIdPawn is { } pawn && pawn.WeaponServices?.ActiveWeapon.Value is { } weapon &&
            weapon.DesignerName == TaserDesignerName)
        {
            TryZapDebounced(shooter, pawn);
        }

        return HookResult.Continue;
    }

    private void TryZapDebounced(IPlayer shooter, CCSPlayerPawn shooterPawn)
    {
        var now = Core.Engine.GlobalVars.CurrentTime;
        var cooldown = Runtime.Config.MasterZeus.ZapCooldownSeconds;

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
    /// Fires (LogInformation, gated behind !debug so it stays silent otherwise) at every decision
    /// point in the extended-range zap - since two rounds of blind guesses at the cause of "deals no
    /// damage at any range" haven't fixed it, the next live test with !debug on will show exactly
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
        var traceOrigin = eyePosition + (forward * MuzzleOffsetDistance);

        IPlayer? bestTarget = null;
        var bestDistanceSquared = float.MaxValue;
        var candidatesConsidered = 0;
        var rejectedOutOfRange = 0;
        var rejectedAimCone = 0;
        var rejectedBlockedLos = 0;

        foreach (var candidate in Core.PlayerManager.GetAlive())
        {
            if (candidate.SteamID == shooter.SteamID || candidate.Controller?.Team == shooter.Controller?.Team ||
                candidate.PlayerPawn is not { } candidatePawn || candidatePawn.AbsOrigin is not { } candidatePosition)
            {
                continue;
            }

            candidatesConsidered++;

            var toCandidate = candidatePosition - traceOrigin;
            var distanceSquared = toCandidate.LengthSquared();
            if (distanceSquared > ExtendedRangeDistance * ExtendedRangeDistance || distanceSquared >= bestDistanceSquared)
            {
                rejectedOutOfRange++;
                continue;
            }

            if (Vector.Dot(forward, toCandidate.Normalized()) < AimConeCosine)
            {
                rejectedAimCone++;
                continue;
            }

            // Bug fix: live !debug logging (StartInSolid=False, HitEntity=func_buyzone) confirmed the
            // trace was blocking on a non-solid trigger volume, not real geometry or a self-hit - buy
            // zones, bomb/rescue zones etc. should never physically block a line of sight.
            // HitTrigger(false) alone didn't stop it (func_buyzone apparently isn't classified as a
            // "trigger" for this trace's HitTrigger flag specifically, despite being non-solid
            // gameplay-wise), so this instead uses ShouldHitEntity to explicitly veto known trigger-
            // zone designer names regardless of how the engine's own mask/trigger classification
            // treats them - a direct, engine-classification-independent exclusion.
            var losParams = TraceParams.Builder(TraceParams.DefaultLine())
                .InteractWith(MaskTrace.Solid)
                .HitTrigger(false)
                .WithShouldHitEntity(entity => entity.DesignerName is not ("func_buyzone" or "func_bomb_target" or "func_hostage_rescue"))
                .IgnoreEntity(shooterPawn)
                .IgnoreEntity(candidatePawn)
                .Build();

            var losResult = Core.Trace.TraceShapeLine(traceOrigin, candidatePosition, losParams);
            if (losResult.DidHit)
            {
                rejectedBlockedLos++;

                if (Runtime.DebugMode)
                {
                    Core.Logger.LogInformation("[CSRoll] MasterZeus LOS blocked - StartInSolid={StartInSolid} HitEntity={Entity} Fraction={Fraction:0.###}",
                        losResult.StartInSolid, losResult.Entity?.DesignerName ?? "null", losResult.Fraction);
                }

                continue;
            }

            bestTarget = candidate;
            bestDistanceSquared = distanceSquared;
        }

        if (bestTarget is null)
        {
            LogZapDebug(shooter, $"no qualifying target ({candidatesConsidered} enemy candidate(s): {rejectedOutOfRange} out of range, {rejectedAimCone} outside aim cone, {rejectedBlockedLos} blocked LOS)");
            return;
        }

        var damage = Runtime.Config.MasterZeus.ZapDamage;
        bestTarget.TakeDamage(damage, DamageTypes_t.DMG_SHOCK, shooterPawn, shooterPawn);
        LogZapDebug(shooter, $"dealt {damage} damage to {(bestTarget.Controller is { IsValid: true } tc ? tc.PlayerName : bestTarget.Slot.ToString())}");
    }
}
