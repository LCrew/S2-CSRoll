using Microsoft.Extensions.Logging;

using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

using CSRoll.Core;

namespace CSRoll.Modifiers;

/// <summary>
/// Bug fix: this used to be resources/ConVarModifiers/SuperJumpModifier.cfg, driving
/// sv_jump_impulse/sv_falldamage_scale - both server-wide, so every player jumped higher and took no
/// fall damage instead of just whoever rolled it. Core.GameHooks.Entities.TakeDamage.Pre zeroes
/// DMG_FALL damage for this player specifically (the same hook mechanism HardHead/IronBody/Revive
/// already use for other per-player damage exceptions) - that part was fine from the start.
///
/// Bug fix 2 (the jump-height half): this went through two failed designs. First it tried to detect
/// "a jump is already in progress" via moveData.Velocity.Z &gt; 0 in ProcessMovement.Pre, but Pre fires
/// before native jump code runs, so that was never true on the tick that mattered. Second, it copied
/// Bhop's Pre-hook velocity injection - live testing ("couldn't tell, nothing seemed to change")
/// suggests a directly-injected Pre-hook velocity for a discrete event like a jump doesn't reliably
/// survive the native code that runs right after it, unlike a passively-respected cap (MaxSpeed).
/// Switched to IGameHookMovement.OnJumpLegacy/OnJumpModern (Post) instead - these fire AFTER the
/// engine's own native jump already applied its normal velocity, so overwriting
/// IMoveData.Velocity.Z here is the last write for that tick rather than something native code can
/// still clobber afterward. Both variants are hooked since it's unconfirmed which of CS2's two
/// parallel jump-input systems ("legacy" vs "modern" subtick) a given server/client uses.
///
/// Reworked into a jetpack per explicit request: this used to let a player keep re-triggering
/// OnJumpLegacy/OnJumpModern while already airborne (nothing checked whether they'd landed yet), so
/// mashing jump mid-air just re-launched them over and over - visually indistinguishable from flying,
/// but as an unintended repeated-jump exploit rather than a designed mechanic.
///
/// Bug fix: the first version of this rework added the new thrust mechanic below but never actually
/// closed the original exploit - BoostJumpVelocity had no ground check at all, so spamming jump
/// instead of holding it still re-triggered the full-height boost every time, completely bypassing
/// fuel. A follow-up attempt gated it on CCSPlayerPawn.OnGroundLastTick, which live testing showed
/// made no difference at all (evaluating "grounded" on every re-trigger regardless of real air
/// state) - see BoostJumpVelocity's own remarks for the elapsed-time cooldown used instead, which
/// doesn't depend on any movement-hook-internal ground state.
///
/// The initial jump off the ground still gets the same height boost as before (now cooldown-limited
/// against spam), but holding jump while airborne instead applies a deliberate, fuel-limited upward
/// thrust via ProcessMovement.Post - the same "last write wins" Post-hook mechanism the original
/// jump-height fix already proved reliable.
///
/// Bug fix: "is thrusting" used to be decided independently in two different places that could
/// disagree - ProcessMovement.Post checked IMoveData.InAir to decide whether to apply the velocity
/// floor, while a separate per-slot flag set BY that same check fed the fuel-drain/gauge logic in
/// OnTick. Reports that the gauge never appeared at all point at InAir not reliably reflecting real
/// air state at that specific hook's timing - the same class of problem OnGroundLastTick turned out
/// to have in BoostJumpVelocity's own history above. Rather than keep trusting an unverified
/// movement-hook-internal field, "is thrusting" is now decided exactly ONCE per real server tick, in
/// OnGameTick, using CBaseEntity.GroundEntity (a CHandle - null Value means airborne, the same
/// handle-nullness pattern already used successfully elsewhere in this codebase, e.g.
/// GameModifierKamikaze's Inflictor check) combined with the jump button state. That single per-tick
/// result is what both applies the velocity floor in ProcessMovement.Post AND drives fuel
/// drain/regen/the gauge - one source of truth instead of two independently-computed signals that
/// could silently disagree. AirAccelerate.Pre separately boosts in-flight steering by multiplying
/// CS2's normal air-accelerate value, satisfying the "add air-strafe" half of the request without
/// needing to fight the physics simulation with a hand-rolled WASD-to-velocity mapping. The gauge is
/// now always shown continuously while the modifier is active (not hidden at full/idle), matching
/// FlankTeleport/ConditionalInvisibility/FullInvisibility's persistent-HUD convention, so there's no
/// ambiguity about whether it's rendering at all.
/// </summary>
public sealed class GameModifierJetpack : GameModifierBase
{
    private const int GaugeBarWidth = 20;

    private readonly Dictionary<int, float> _fuel = [];
    private readonly Dictionary<int, bool> _isThrusting = [];
    private readonly Dictionary<int, float> _nextGaugeUpdateTime = [];
    private readonly Dictionary<int, float> _lastBigBoostTime = [];

    private float _lastTickTime = -1f;
    private Guid _spawnHookId;

    public GameModifierJetpack()
    {
        Name = "Jetpack";
        Description = "Jumping is much higher, no fall damage, and holding jump in the air fires a fuel-limited jetpack thrust with boosted air-strafe";
        SupportsRandomRounds = true;
        SupportsPerPlayerRandomization = true;
    }

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
        Core.GameHooks.Movement.OnJumpLegacy.Post += OnJumpLegacy;
        Core.GameHooks.Movement.OnJumpModern.Post += OnJumpModern;
        Core.GameHooks.Movement.ProcessMovement.Post += OnProcessMovement;
        Core.GameHooks.Movement.AirAccelerate.Pre += OnAirAccelerate;
        Core.GameHooks.Entities.TakeDamage.Pre += OnTakeDamage;
        Core.Event.OnTick += OnGameTick;
        _spawnHookId = Core.GameEvent.HookPost<EventPlayerSpawn>(OnPlayerSpawn);

        _lastTickTime = -1f;

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (IsAssignedTo(player.Slot))
            {
                _fuel[player.Slot] = Runtime.Config.Jetpack.MaxFuel;
            }
        }
    }

    protected override void OnDisabled()
    {
        Core.GameHooks.Movement.OnJumpLegacy.Post -= OnJumpLegacy;
        Core.GameHooks.Movement.OnJumpModern.Post -= OnJumpModern;
        Core.GameHooks.Movement.ProcessMovement.Post -= OnProcessMovement;
        Core.GameHooks.Movement.AirAccelerate.Pre -= OnAirAccelerate;
        Core.GameHooks.Entities.TakeDamage.Pre -= OnTakeDamage;
        Core.Event.OnTick -= OnGameTick;
        Core.GameEvent.Unhook(_spawnHookId);

        _fuel.Clear();
        _isThrusting.Clear();
        _nextGaugeUpdateTime.Clear();
        _lastBigBoostTime.Clear();
    }

    private void OnJumpLegacy(ref OnJumpLegacyMovementPostContext ctx) => BoostJumpVelocity(ctx.Params.Player, ctx.Params.MoveData, "legacy");

    private void OnJumpModern(ref OnJumpModernMovementPostContext ctx) => BoostJumpVelocity(ctx.Params.Player, ctx.Params.MoveData, "modern");

    private void BoostJumpVelocity(IPlayer? player, IMoveData moveData, string variant)
    {
        if (player is not { IsValid: true } || !IsAssignedTo(player.Slot))
        {
            return;
        }

        // Bug fix: OnJumpLegacy/OnJumpModern can fire again while already airborne - that's exactly
        // the original pre-jetpack "mash space to keep re-launching" exploit described in the
        // class-level history above, and nothing here was actually guarding against it. Two attempts
        // at a ground-state check here (IMoveData.InAir, then CCSPlayerPawn.OnGroundLastTick) both
        // turned out unreliable at this specific Post-hook's timing - live testing of the
        // OnGroundLastTick version showed literally no behavior change at all, meaning it was
        // evaluating as "grounded" on every single re-trigger regardless of real air state. Rather
        // than guess a third pawn/moveData field, this instead rate-limits the big boost with a
        // plain elapsed-time cooldown (Core.Engine.GlobalVars.CurrentTime, the same debounce
        // mechanism already proven reliable for MasterZeus/FlankTeleport) - no dependency on any
        // movement-hook-internal state at all. Spamming space can now only re-trigger the big boost
        // once per BigBoostCooldownSeconds; holding space for sustained lift is unaffected, since
        // that's the separate, uncapped-duration thrust mechanic in OnProcessMovement below.
        var now = Core.Engine.GlobalVars.CurrentTime;
        if (_lastBigBoostTime.TryGetValue(player.Slot, out var lastBoostTime) &&
            now - lastBoostTime < Runtime.Config.Jetpack.BigBoostCooldownSeconds)
        {
            return;
        }

        _lastBigBoostTime[player.Slot] = now;

        moveData.Velocity = new Vector(moveData.Velocity.X, moveData.Velocity.Y, Runtime.Config.Jetpack.JumpVelocityZ);

        if (Runtime.DebugMode)
        {
            Core.Logger.LogInformation("[CSRoll] Jetpack ({Slot}): {Variant} jump boosted, set VelocityZ={VelZ}", player.Slot, variant, Runtime.Config.Jetpack.JumpVelocityZ);
        }
    }

    private void OnProcessMovement(ref ProcessMovementMovementPostContext ctx)
    {
        var player = ctx.Params.Player;
        if (player is not { IsValid: true } || !IsAssignedTo(player.Slot))
        {
            return;
        }

        // "Is thrusting" is decided once per real tick by OnGameTick (see class-level bug-fix note) -
        // this just applies it. Safe to call more than once per server tick (CS2's subtick movement
        // can invoke ProcessMovement several times per tick) since flooring is idempotent.
        if (!_isThrusting.GetValueOrDefault(player.Slot, false))
        {
            return;
        }

        var moveData = ctx.Params.MoveData;
        var velocity = moveData.Velocity;
        if (velocity.Z < Runtime.Config.Jetpack.ThrustSpeed)
        {
            moveData.Velocity = new Vector(velocity.X, velocity.Y, Runtime.Config.Jetpack.ThrustSpeed);
        }
    }

    private void OnAirAccelerate(ref AirAccelerateMovementPreContext ctx)
    {
        var player = ctx.Params.Player;
        if (player is not { IsValid: true } || !IsAssignedTo(player.Slot))
        {
            return;
        }

        ctx.Params.Acceleration *= Runtime.Config.Jetpack.AirStrafeMultiplier;
    }

    private void OnTakeDamage(ref TakeDamageEntityPreContext ctx)
    {
        if ((ctx.Params.Info.DamageType & DamageTypes_t.DMG_FALL) == 0)
        {
            return;
        }

        var victim = Core.PlayerManager.GetPlayerFromPawn(ctx.Params.Entity.As<CBasePlayerPawn>());
        if (victim is not { IsValid: true } || !IsAssignedTo(victim.Slot))
        {
            return;
        }

        ctx.Params.Info.Damage = 0;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event)
    {
        if (@event.UserIdPlayer is { IsValid: true } player && IsAssignedTo(player.Slot))
        {
            _fuel[player.Slot] = Runtime.Config.Jetpack.MaxFuel;
        }

        return HookResult.Continue;
    }

    /// <summary>
    /// Once-per-server-tick: decides "is thrusting" (the single source of truth ProcessMovement.Post
    /// consumes - see class-level bug-fix note), drains/regens fuel from that decision, and refreshes
    /// the gauge. Deliberately not done inside OnProcessMovement, which can fire more than once per
    /// tick and would otherwise double-count drain/regen on those ticks.
    /// </summary>
    private void OnGameTick()
    {
        var now = Core.Engine.GlobalVars.CurrentTime;
        var deltaSeconds = _lastTickTime < 0f ? 0f : Math.Max(0f, now - _lastTickTime);
        _lastTickTime = now;

        foreach (var player in Core.PlayerManager.GetAllValidPlayers())
        {
            if (!IsAssignedTo(player.Slot))
            {
                continue;
            }

            var slot = player.Slot;
            var maxFuel = Runtime.Config.Jetpack.MaxFuel;
            var fuel = _fuel.GetValueOrDefault(slot, maxFuel);

            // CBaseEntity.GroundEntity is a CHandle - a null Value means airborne. This is the
            // long-established, already-proven-reliable Source-engine ground check (the same
            // handle-nullness pattern used successfully elsewhere in this codebase), read here during
            // normal tick processing rather than from inside any movement-hook's own timing.
            var isAirborne = player.PlayerPawn?.GroundEntity.Value is null;
            var isHoldingJump = player.PressedButtons.HasFlag(GameButtonFlags.Space);
            var isThrusting = isAirborne && isHoldingJump && fuel > 0f;
            _isThrusting[slot] = isThrusting;

            if (Runtime.DebugMode)
            {
                Core.Logger.LogInformation("[CSRoll] Jetpack ({Slot}): airborne={Airborne} holdingJump={HoldingJump} fuel={Fuel:0.#} thrusting={Thrusting}",
                    slot, isAirborne, isHoldingJump, fuel, isThrusting);
            }

            fuel = isThrusting
                ? Math.Max(0f, fuel - (Runtime.Config.Jetpack.FuelDrainPerSecond * deltaSeconds))
                : Math.Min(maxFuel, fuel + (Runtime.Config.Jetpack.FuelRegenPerSecond * deltaSeconds));

            _fuel[slot] = fuel;

            UpdateFuelGauge(player, fuel, maxFuel);
        }
    }

    /// <summary>Always shown while the modifier is active (not hidden at full/idle) - matching FlankTeleport/ConditionalInvisibility/FullInvisibility's persistent-HUD convention, so there's no ambiguity about whether it's rendering.</summary>
    private void UpdateFuelGauge(IPlayer player, float fuel, float maxFuel)
    {
        var now = Core.Engine.GlobalVars.CurrentTime;
        var interval = Runtime.Config.Jetpack.GaugeUpdateIntervalSeconds;
        if (_nextGaugeUpdateTime.TryGetValue(player.Slot, out var nextUpdate) && now < nextUpdate)
        {
            return;
        }

        _nextGaugeUpdateTime[player.Slot] = now + interval;
        player.SendCenterHTML(BuildFuelGaugeHtml(fuel, maxFuel), (int)((interval * 1000) + 100));
    }

    private static string BuildFuelGaugeHtml(float fuel, float maxFuel)
    {
        var ratio = maxFuel > 0f ? fuel / maxFuel : 0f;
        return CSRollUtils.BuildGaugeHtml("Jetpack Fuel", "gold", ratio, CSRollUtils.GetGaugeBarColor(ratio), GaugeBarWidth);
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        _fuel.Remove(@event.PlayerId);
        _isThrusting.Remove(@event.PlayerId);
        _nextGaugeUpdateTime.Remove(@event.PlayerId);
        _lastBigBoostTime.Remove(@event.PlayerId);
    }
}
